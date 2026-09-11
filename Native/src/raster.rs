use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::{Arc, Mutex, OnceLock};

use fontdb::{Database, Family, Query, Source as FontSource, Stretch, Style, Weight};
use serde::Deserialize;
use svgtypes::{SimplePathSegment, SimplifyingPathParser};
use swash::scale::{Render, ScaleContext, Source};
use swash::shape::{Direction, ShapeContext};
use swash::zeno::{Format, Vector};
use swash::{FontRef, GlyphId};
use tiny_skia::{FillRule, Paint, PathBuilder, Pixmap, Transform};

#[derive(Debug, Clone, Deserialize)]
pub struct TextRasterRequest {
    pub text: String,
    #[serde(default)]
    pub font_family: String,
    #[serde(default)]
    pub font_file: String,
    #[serde(default)]
    pub bold: bool,
    pub pixel_size: u32,
    pub line_height: u32,
    pub red: u8,
    pub green: u8,
    pub blue: u8,
}

#[derive(Debug, Clone)]
pub struct RasterImage {
    pub pixels: Vec<u8>,
    pub width: u32,
    pub height: u32,
    pub texture_offset_x: f32,
    pub texture_offset_y: f32,
    pub layout_width: f32,
    pub layout_height: f32,
    pub visual_x: f32,
    pub visual_y: f32,
    pub visual_width: f32,
    pub visual_height: f32,
}

#[derive(Clone)]
struct OwnedFont {
    data: Arc<Vec<u8>>,
    index: usize,
}

struct RasterState {
    database: Database,
    fonts: HashMap<String, OwnedFont>,
    shape_context: ShapeContext,
    scale_context: ScaleContext,
}

impl RasterState {
    fn new() -> Self {
        let mut database = Database::new();
        database.load_system_fonts();
        // fontdb intentionally skips Android's fontconfig scan. The game host
        // exposes the platform fonts at these stable paths; loading them keeps
        // the material UI usable without requiring a user supplied font file.
        #[cfg(target_os = "android")]
        for directory in ["/system/fonts", "/system/product/fonts", "/product/fonts"] {
            database.load_fonts_dir(directory);
        }
        Self {
            database,
            fonts: HashMap::new(),
            shape_context: ShapeContext::new(),
            scale_context: ScaleContext::new(),
        }
    }

    fn font(&mut self, request: &TextRasterRequest) -> Result<OwnedFont, String> {
        let cache_key = if request.font_file.trim().is_empty() {
            format!("family:{}:{}", request.font_family.trim(), request.bold)
        } else {
            format!("file:{}:{}", request.font_file.trim(), request.bold)
        };
        if let Some(font) = self.fonts.get(&cache_key) {
            return Ok(font.clone());
        }

        let font = if !request.font_file.trim().is_empty() {
            let path = PathBuf::from(request.font_file.trim());
            let data = std::fs::read(&path)
                .map_err(|error| format!("cannot read font {}: {error}", path.display()))?;
            validate_font(data, 0)?
        } else {
            let family_name = if request.font_family.trim().is_empty() {
                "Microsoft YaHei UI"
            } else {
                request.font_family.trim()
            };
            let families = [Family::Name(family_name), Family::SansSerif];
            let id = self
                .database
                .query(&Query {
                    families: &families,
                    weight: if request.bold {
                        Weight::BOLD
                    } else {
                        Weight::NORMAL
                    },
                    stretch: Stretch::Normal,
                    style: Style::Normal,
                })
                .ok_or_else(|| format!("font family '{family_name}' was not found"))?;
            let (source, index) = self
                .database
                .face_source(id)
                .ok_or_else(|| format!("font family '{family_name}' has no readable source"))?;
            let data = source_bytes(source)?;
            validate_font(data, index as usize)?
        };
        self.fonts.insert(cache_key, font.clone());
        Ok(font)
    }
}

fn validate_font(data: Vec<u8>, index: usize) -> Result<OwnedFont, String> {
    if FontRef::from_index(&data, index).is_none() {
        return Err(format!("font face index {index} is invalid"));
    }
    Ok(OwnedFont {
        data: Arc::new(data),
        index,
    })
}

fn source_bytes(source: FontSource) -> Result<Vec<u8>, String> {
    match source {
        FontSource::Binary(data) => Ok(data.as_ref().as_ref().to_vec()),
        FontSource::File(path) => std::fs::read(&path)
            .map_err(|error| format!("cannot read font {}: {error}", path.display())),
        FontSource::SharedFile(_, data) => Ok(data.as_ref().as_ref().to_vec()),
    }
}

static RASTER_STATE: OnceLock<Mutex<RasterState>> = OnceLock::new();

fn raster_state() -> &'static Mutex<RasterState> {
    RASTER_STATE.get_or_init(|| Mutex::new(RasterState::new()))
}

#[cfg(windows)]
pub fn font_families() -> Result<Vec<String>, String> {
    crate::dwrite_raster::font_families()
}

#[cfg(not(windows))]
pub fn font_families() -> Result<Vec<String>, String> {
    let state = raster_state()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let mut families: Vec<String> = state
        .database
        .faces()
        .flat_map(|face| face.families.iter().map(|(name, _)| name.clone()))
        .filter(|name| !name.trim().is_empty())
        .collect();
    families.sort_by_key(|name| name.to_lowercase());
    families.dedup_by(|left, right| left.eq_ignore_ascii_case(right));
    // The managed catalog preserves this first entry as the platform recommendation before
    // alphabetizing the font picker. Avoid making an arbitrary Latin-only family the fallback.
    if let Some(recommended) = recommended_font_family(&state.database) {
        if let Some(index) = families
            .iter()
            .position(|name| name.eq_ignore_ascii_case(&recommended))
        {
            let recommended = families.remove(index);
            families.insert(0, recommended);
        }
    }
    Ok(families)
}

#[cfg(not(windows))]
fn recommended_font_family(database: &Database) -> Option<String> {
    const PREFERRED_CJK_FAMILIES: &[&str] = &[
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Noto Sans CJK SC",
        "Noto Sans SC",
        "Source Han Sans CN",
        "Source Han Sans SC",
        "WenQuanYi Micro Hei",
        "WenQuanYi Zen Hei",
        "PingFang SC",
        "Hiragino Sans GB",
        "Droid Sans Fallback",
        "Arial Unicode MS",
    ];

    // Prefer the user's fontconfig sans-serif choice when it actually covers Chinese.
    let generic_families = [Family::SansSerif];
    if let Some(id) = database.query(&Query {
        families: &generic_families,
        weight: Weight::NORMAL,
        stretch: Stretch::Normal,
        style: Style::Normal,
    }) {
        if font_cjk_coverage(database, id) == CJK_SAMPLE.len() {
            if let Some(family) = database
                .face(id)
                .and_then(|face| face.families.first())
                .map(|(name, _)| name.clone())
            {
                return Some(family);
            }
        }
    }

    for family_name in PREFERRED_CJK_FAMILIES {
        let families = [Family::Name(family_name)];
        let Some(id) = database.query(&Query {
            families: &families,
            weight: Weight::NORMAL,
            stretch: Stretch::Normal,
            style: Style::Normal,
        }) else {
            continue;
        };
        if font_cjk_coverage(database, id) == CJK_SAMPLE.len() {
            return Some((*family_name).to_owned());
        }
    }

    database
        .faces()
        .filter_map(|face| {
            let family = face.families.first()?.0.clone();
            let coverage = font_cjk_coverage(database, face.id);
            if coverage == 0 {
                return None;
            }
            let style_penalty = usize::from(face.style != Style::Normal);
            let weight_penalty = face.weight.0.abs_diff(Weight::NORMAL.0) as usize;
            let monospace_penalty = usize::from(face.monospaced);
            Some((
                family.to_lowercase(),
                family,
                coverage,
                style_penalty,
                weight_penalty,
                monospace_penalty,
            ))
        })
        .max_by(|left, right| {
            left.2
                .cmp(&right.2)
                .then_with(|| right.3.cmp(&left.3))
                .then_with(|| right.4.cmp(&left.4))
                .then_with(|| right.5.cmp(&left.5))
                .then_with(|| right.0.cmp(&left.0))
        })
        .map(|(_, family, _, _, _, _)| family)
}

#[cfg(not(windows))]
const CJK_SAMPLE: &[char] = &['中', '文', '简', '体', '界', '面'];

#[cfg(not(windows))]
fn font_cjk_coverage(database: &Database, id: fontdb::ID) -> usize {
    database
        .with_face_data(id, |data, index| {
            let Some(font) = FontRef::from_index(data, index as usize) else {
                return 0;
            };
            let charmap = font.charmap();
            CJK_SAMPLE
                .iter()
                .filter(|character| charmap.map(**character) != 0)
                .count()
        })
        .unwrap_or(0)
}

#[derive(Clone)]
struct PositionedGlyph {
    image: swash::scale::image::Image,
    left: i32,
    top: i32,
}

pub fn rasterize_text(request: &TextRasterRequest) -> Result<RasterImage, String> {
    if request.pixel_size == 0 || request.line_height == 0 {
        return Err("pixel_size and line_height must be positive".to_owned());
    }
    #[cfg(windows)]
    if request.font_file.trim().is_empty() {
        return crate::dwrite_raster::rasterize_text(request);
    }
    let mut state = raster_state()
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());
    let owned = state.font(request)?;
    let font = FontRef::from_index(&owned.data, owned.index)
        .ok_or_else(|| "font became invalid".to_owned())?;
    let mut positioned = Vec::new();
    let mut layout_width = 0.0f32;
    let normalized_text = request.text.replace('\r', "");
    let lines: Vec<&str> = normalized_text.split('\n').collect();

    for (line_index, line) in lines.iter().enumerate() {
        let mut glyphs = Vec::new();
        let mut shaper = state
            .shape_context
            .builder(font)
            .size(request.pixel_size as f32)
            .direction(Direction::LeftToRight)
            .build();
        shaper.add_str(line);
        let metrics = shaper.metrics();
        shaper.shape_with(|cluster| glyphs.extend_from_slice(cluster.glyphs));

        let mut scaler = state
            .scale_context
            .builder(font)
            .size(request.pixel_size as f32)
            .hint(true)
            .build();
        let font_height = metrics.ascent + metrics.descent;
        let baseline = line_index as f32 * request.line_height as f32
            + (request.line_height as f32 - font_height) * 0.5
            + metrics.ascent;
        let mut cursor_x = 0.0f32;
        for glyph in glyphs {
            let origin_x = cursor_x + glyph.x;
            let origin_y = baseline - glyph.y;
            let offset = Vector::new(0.0, 0.0);
            if let Some(image) = Render::new(&[Source::Outline])
                .format(Format::Alpha)
                .offset(offset)
                .render(&mut scaler, glyph.id as GlyphId)
            {
                if image.placement.width != 0 && image.placement.height != 0 {
                    positioned.push(PositionedGlyph {
                        left: origin_x.floor() as i32 + image.placement.left,
                        top: origin_y.floor() as i32 - image.placement.top,
                        image,
                    });
                }
            }
            cursor_x += glyph.advance;
        }
        layout_width = layout_width.max(cursor_x);
    }

    let layout_height = lines.len() as f32 * request.line_height as f32;
    if positioned.is_empty() {
        return Ok(RasterImage {
            pixels: Vec::new(),
            width: 0,
            height: 0,
            texture_offset_x: 0.0,
            texture_offset_y: 0.0,
            layout_width,
            layout_height,
            visual_x: 0.0,
            visual_y: 0.0,
            visual_width: 0.0,
            visual_height: 0.0,
        });
    }

    let left = positioned.iter().map(|glyph| glyph.left).min().unwrap_or(0);
    let top = positioned.iter().map(|glyph| glyph.top).min().unwrap_or(0);
    let right = positioned
        .iter()
        .map(|glyph| glyph.left + glyph.image.placement.width as i32)
        .max()
        .unwrap_or(left);
    let bottom = positioned
        .iter()
        .map(|glyph| glyph.top + glyph.image.placement.height as i32)
        .max()
        .unwrap_or(top);
    let width = (right - left).max(0) as u32;
    let height = (bottom - top).max(0) as u32;
    let mut pixels = vec![0u8; width as usize * height as usize * 4];
    for glyph in positioned {
        composite_mask(
            &mut pixels,
            width,
            height,
            glyph.left - left,
            glyph.top - top,
            &glyph.image,
            request.red,
            request.green,
            request.blue,
        );
    }
    Ok(RasterImage {
        pixels,
        width,
        height,
        texture_offset_x: left as f32,
        texture_offset_y: top as f32,
        layout_width,
        layout_height,
        visual_x: left as f32,
        visual_y: top as f32,
        visual_width: width as f32,
        visual_height: height as f32,
    })
}

#[allow(clippy::too_many_arguments)]
fn composite_mask(
    target: &mut [u8],
    target_width: u32,
    target_height: u32,
    left: i32,
    top: i32,
    image: &swash::scale::image::Image,
    red: u8,
    green: u8,
    blue: u8,
) {
    let width = image.placement.width as i32;
    let height = image.placement.height as i32;
    for y in 0..height {
        let target_y = top + y;
        if target_y < 0 || target_y >= target_height as i32 {
            continue;
        }
        for x in 0..width {
            let target_x = left + x;
            if target_x < 0 || target_x >= target_width as i32 {
                continue;
            }
            let source = y as usize * width as usize + x as usize;
            let alpha = image.data.get(source).copied().unwrap_or(0) as u32;
            if alpha == 0 {
                continue;
            }
            let destination = (target_y as usize * target_width as usize + target_x as usize) * 4;
            let inverse = 255 - alpha;
            let source_blue = blue as u32 * alpha / 255;
            let source_green = green as u32 * alpha / 255;
            let source_red = red as u32 * alpha / 255;
            target[destination] = (source_blue + target[destination] as u32 * inverse / 255) as u8;
            target[destination + 1] =
                (source_green + target[destination + 1] as u32 * inverse / 255) as u8;
            target[destination + 2] =
                (source_red + target[destination + 2] as u32 * inverse / 255) as u8;
            target[destination + 3] =
                (alpha + target[destination + 3] as u32 * inverse / 255) as u8;
        }
    }
}

pub fn rasterize_svg(
    svg: &str,
    pixel_size: u32,
    red: u8,
    green: u8,
    blue: u8,
) -> Result<RasterImage, String> {
    const OVERSAMPLE: u32 = 48;
    const OFFSET_X: f32 = 0.00;
    const OFFSET_Y: f32 = 0.00;
    if pixel_size == 0 {
        return Err("pixel_size must be positive".to_owned());
    }
    let document = roxmltree::Document::parse(svg)
        .map_err(|error| format!("invalid SVG document: {error}"))?;
    let root = document.root_element();
    let view_box = parse_view_box(root.attribute("viewBox").unwrap_or("0 0 24 24"))?;
    if view_box.2 <= 0.0 || view_box.3 <= 0.0 {
        return Err("SVG viewBox has non-positive dimensions".to_owned());
    }
    let raster_size = pixel_size
        .checked_mul(OVERSAMPLE)
        .ok_or_else(|| "SVG raster size overflow".to_owned())?;
    let scale = (raster_size as f32 / view_box.2).min(raster_size as f32 / view_box.3);
    let translate_x =
        (raster_size as f32 - view_box.2 * scale) * 0.5 - view_box.0 * scale + OFFSET_X;
    let translate_y =
        (raster_size as f32 - view_box.3 * scale) * 0.5 - view_box.1 * scale + OFFSET_Y;
    let mut pixmap = Pixmap::new(raster_size, raster_size)
        .ok_or_else(|| "cannot allocate SVG pixmap".to_owned())?;
    let mut paint = Paint::default();
    paint.set_color_rgba8(red, green, blue, 255);
    paint.anti_alias = true;
    for node in root.descendants().filter(|node| node.has_tag_name("path")) {
        let Some(data) = node.attribute("d") else {
            continue;
        };
        let path = build_path(data, scale, translate_x, translate_y)?;
        pixmap.fill_path(
            &path,
            &paint,
            FillRule::Winding,
            Transform::identity(),
            None,
        );
    }
    let mut pixels = vec![0u8; pixel_size as usize * pixel_size as usize * 4];
    let samples = (OVERSAMPLE * OVERSAMPLE) as u32;
    for y in 0..pixel_size {
        for x in 0..pixel_size {
            let mut blue_sum = 0u32;
            let mut green_sum = 0u32;
            let mut red_sum = 0u32;
            let mut alpha = 0u32;
            for sample_y in 0..OVERSAMPLE {
                for sample_x in 0..OVERSAMPLE {
                    let source_x = x * OVERSAMPLE + sample_x;
                    let source_y = y * OVERSAMPLE + sample_y;
                    let color = pixmap.pixels()[(source_y * raster_size + source_x) as usize];
                    blue_sum += color.blue() as u32;
                    green_sum += color.green() as u32;
                    red_sum += color.red() as u32;
                    alpha += color.alpha() as u32;
                }
            }
            let destination = (y as usize * pixel_size as usize + x as usize) * 4;
            let coverage = ((alpha + samples / 2) / samples) as u8;
            pixels[destination] = ((blue_sum + samples / 2) / samples) as u8;
            pixels[destination + 1] = ((green_sum + samples / 2) / samples) as u8;
            pixels[destination + 2] = ((red_sum + samples / 2) / samples) as u8;
            pixels[destination + 3] = coverage;
        }
    }
    Ok(RasterImage {
        pixels,
        width: pixel_size,
        height: pixel_size,
        texture_offset_x: 0.0,
        texture_offset_y: 0.0,
        layout_width: pixel_size as f32,
        layout_height: pixel_size as f32,
        visual_x: 0.0,
        visual_y: 0.0,
        visual_width: pixel_size as f32,
        visual_height: pixel_size as f32,
    })
}

fn parse_view_box(value: &str) -> Result<(f32, f32, f32, f32), String> {
    let values: Vec<f32> = value
        .split(|character: char| character.is_ascii_whitespace() || character == ',')
        .filter(|part| !part.is_empty())
        .map(|part| part.parse::<f32>().map_err(|error| error.to_string()))
        .collect::<Result<_, _>>()?;
    if values.len() != 4 {
        return Err("SVG viewBox must contain four numbers".to_owned());
    }
    Ok((values[0], values[1], values[2], values[3]))
}

fn build_path(
    data: &str,
    scale: f32,
    translate_x: f32,
    translate_y: f32,
) -> Result<tiny_skia::Path, String> {
    let mut builder = PathBuilder::new();
    for segment in SimplifyingPathParser::from(data) {
        let segment = segment.map_err(|error| format!("invalid SVG path: {error}"))?;
        match segment {
            SimplePathSegment::MoveTo { x, y } => builder.move_to(
                x as f32 * scale + translate_x,
                y as f32 * scale + translate_y,
            ),
            SimplePathSegment::LineTo { x, y } => builder.line_to(
                x as f32 * scale + translate_x,
                y as f32 * scale + translate_y,
            ),
            SimplePathSegment::CurveTo {
                x1,
                y1,
                x2,
                y2,
                x,
                y,
            } => builder.cubic_to(
                x1 as f32 * scale + translate_x,
                y1 as f32 * scale + translate_y,
                x2 as f32 * scale + translate_x,
                y2 as f32 * scale + translate_y,
                x as f32 * scale + translate_x,
                y as f32 * scale + translate_y,
            ),
            SimplePathSegment::Quadratic { x1, y1, x, y } => builder.quad_to(
                x1 as f32 * scale + translate_x,
                y1 as f32 * scale + translate_y,
                x as f32 * scale + translate_x,
                y as f32 * scale + translate_y,
            ),
            SimplePathSegment::ClosePath => builder.close(),
        }
    }
    builder
        .finish()
        .ok_or_else(|| "SVG path is empty".to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rasterizes_svg() {
        let image = rasterize_svg(
            r#"<svg viewBox="0 0 24 24"><path d="M2 2h20v20H2Z"/></svg>"#,
            24,
            255,
            255,
            255,
        )
        .unwrap();
        assert_eq!(image.pixels.len(), 24 * 24 * 4);
        assert!(image.pixels.iter().any(|value| *value != 0));
    }
}
