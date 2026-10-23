namespace Celeste.Mod.MicroblocksQolUtils;

internal interface IMaterialAcrylicPage {
    bool SuppressNormalRender { get; set; }
    void RenderMaterialContent(bool acrylicActive);
}

internal interface IMaterialAcrylicOverlay {
    bool HasOverlaysForAcrylic { get; }
    void HideOverlaysForAcrylicCapture();
    void RestoreOverlaysAfterAcrylicCapture();
    void RenderOverlaysAboveAcrylic();
}
