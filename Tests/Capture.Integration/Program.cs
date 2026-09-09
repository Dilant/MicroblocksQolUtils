using System.Reflection;
using System.Runtime.InteropServices;
using Celeste.Mod.MicroblocksQolUtils;

string root = Environment.GetEnvironmentVariable("CELESTE_ROOT") ?? throw new Exception("Set CELESTE_ROOT");
string native = Environment.GetEnvironmentVariable("MQOL_NATIVE_PATH") ?? throw new Exception("Set MQOL_NATIVE_PATH");
string encoder = Environment.GetEnvironmentVariable("MQOL_TEST_ENCODER") ?? "libopenh264";
string output = Path.GetFullPath(Environment.GetEnvironmentVariable("MQOL_TEST_OUTPUT") ?? throw new Exception("Set MQOL_TEST_OUTPUT under .work"));
Directory.CreateDirectory(output);
nint Resolve(string name, Assembly assembly, DllImportSearchPath? paths) {
    if (name == "microblocks_qol_native") return NativeLibrary.Load(native);
    string file = name switch {"fmod" or "fmod64" => "fmod64.dll", "fmodstudio" => "fmodstudio.dll", "SDL2" => "SDL2.dll", _ => name};
    string path = Path.Combine(root, "lib64-win-x64", file);
    return File.Exists(path) ? NativeLibrary.Load(path) : 0;
}
NativeLibrary.SetDllImportResolver(typeof(CaptureSource).Assembly, Resolve);
NativeLibrary.SetDllImportResolver(typeof(Celeste.Audio).Assembly, Resolve);
NativeLibrary.SetDllImportResolver(typeof(Microsoft.Xna.Framework.Game).Assembly, Resolve);
// DynDll resolves through the assembly loader, and the native module must be available by name too.
NativeLibrary.Load(Path.Combine(root,"lib64-win-x64","SDL2.dll"));
void Check(bool condition,string text) {if (!condition) throw new Exception(text);}
void Fmod(FMOD.RESULT value) {Check(value==FMOD.RESULT.OK,$"FMOD {value}");}
Check(Sdl.Init(0x20)==0,"SDL init failed");
Sdl.Attribute(17,3); Sdl.Attribute(18,2); Sdl.Attribute(21,1); Sdl.Attribute(5,1);
nint window=Sdl.CreateWindow("Celeste capture integration",0,0,160,90,0x00000002|0x00000008|0x20);
Check(window!=0,"SDL hidden window creation failed");
nint context=Sdl.CreateContext(window); Check(context!=0,"GL 3.2 context creation failed");
Sdl.SwapInterval(0);
var clearColor=Marshal.GetDelegateForFunctionPointer<Sdl.ClearColor>(Sdl.GetProc("glClearColor"));
var clear=Marshal.GetDelegateForFunctionPointer<Sdl.Clear>(Sdl.GetProc("glClear"));
var enable=Marshal.GetDelegateForFunctionPointer<Sdl.Enable>(Sdl.GetProc("glEnable"));
var disable=Marshal.GetDelegateForFunctionPointer<Sdl.Enable>(Sdl.GetProc("glDisable"));
var scissor=Marshal.GetDelegateForFunctionPointer<Sdl.Scissor>(Sdl.GetProc("glScissor"));
Fmod(FMOD.Studio.System.create(out var studio));
Fmod(studio.getLowLevelSystem(out var lowLevel));
Fmod(lowLevel.setSoftwareFormat(48000, FMOD.SPEAKERMODE._7POINT1, 0));
Fmod(studio.initialize(128,FMOD.Studio.INITFLAGS.NORMAL,FMOD.INITFLAGS.NORMAL,0));
foreach(var bank in new[]{"Master Bank.bank","Master Bank.strings.bank","ui.bank","music.bank","sfx.bank"})
    Fmod(studio.loadBankFile(Path.Combine(root,"Content","FMOD","Desktop",bank),FMOD.Studio.LOAD_BANK_FLAGS.NORMAL,out _));
typeof(Celeste.Audio).GetField("system",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,studio);
Fmod(studio.getEvent("event:/ui/main/button_select",out var description));
Fmod(studio.getEvent("event:/char/madeline/jump",out var jumpDescription));
NativeCaptureBridge.Initialize(null); Check(NativeCaptureBridge.Available,"native load failed");
CaptureSource.Load(); Check(CaptureSource.VideoError is null,$"hook failed: {CaptureSource.VideoError}");
long frameCount=0,audioCount=0; ulong lastSequence=0; int badPixels=0, surroundChunks=0, bgmChunks=0;
var musicChanges = new System.Collections.Concurrent.ConcurrentQueue<CaptureMusic>();
Fmod(studio.getEvent("event:/music/menu/level_select", out var musicDescription));
Fmod(musicDescription.createInstance(out var song));
typeof(Celeste.Audio).GetField("currentMusicEvent",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,song);
Fmod(song.start());
using var observer=CaptureSource.Subscribe(frame=> {
    if(frame.Sequence<=lastSequence) Interlocked.Increment(ref badPixels);
    lastSequence=frame.Sequence;
    var pixels=frame.Pixels.Span;
    // Top half blue, bottom half red verifies channel conversion and vertical orientation.
    if(pixels[0]!=255||pixels[2]!=0||pixels[^4]!=0||pixels[^2]!=255) Interlocked.Increment(ref badPixels);
    Interlocked.Increment(ref frameCount);
}, chunk=>{
    if(chunk.Channels==8) Interlocked.Increment(ref surroundChunks);
    if(chunk.Samples.Span.ContainsAnyExcept(0f)) {
        Interlocked.Increment(ref audioCount);
        if(chunk.BusId==3) Interlocked.Increment(ref bgmChunks);
    }
}, change=>musicChanges.Enqueue(change));
using var slow=CaptureSource.Subscribe(_=>Thread.Sleep(120));
var first=NativeCaptureBridge.StartRecording(30,Path.Combine(output,"first.mkv"),encoder,1000);
NativeCaptureSession? second=null;
ulong before=0;
for(int i=0;i<240;i++) {
    Sdl.Pump(); CaptureSource.Update(); Fmod(studio.update());
    Fmod(song.setParameterValue("fade", i >= 150 && i < 155 ? 0.5f : 1f));
    if(i%30==0) {Fmod(description.createInstance(out var sound));Fmod(sound.start());Fmod(sound.release()); Fmod(jumpDescription.createInstance(out var jump));Fmod(jump.start());Fmod(jump.release());}
    if(i==60) second=NativeCaptureBridge.StartRecording(60,Path.Combine(output,"second.mkv"),encoder,1000);
    if(i==80) Fmod(song.setPaused(true));
    if(i==90) Fmod(song.setPaused(false));
    if(i==100) Fmod(song.setTimelinePosition(500));
    if(i==160) { Fmod(song.stop(FMOD.Studio.STOP_MODE.IMMEDIATE)); Fmod(song.start()); }
    if(i==120) {Parallel.Invoke(first.Dispose, first.Dispose); before=second!.Statistics.FramesCaptured; Sdl.SetWindowSize(window,192,108);}
    if(i==210) {Check(second!.Statistics.FramesCaptured>before,"second stopped when first unsubscribed");}
    Sdl.Drawable(window,out int w,out int h);
    disable(0x0C11);clearColor(1,0,0,1);clear(0x4000);
    enable(0x0C11);scissor(0,h/2,w,h-h/2);clearColor(0,0,1,1);clear(0x4000);disable(0x0C11);
    Sdl.Swap(window);Thread.Sleep(16);
}
second!.Dispose(); first.Dispose();
observer.Dispose();observer.Completion.GetAwaiter().GetResult();
slow.Dispose();slow.Completion.GetAwaiter().GetResult();
CaptureSource.Update();
Check(frameCount>100 && audioCount>0,$"no callbacks: {frameCount} frames, {audioCount} audio; {CaptureSource.VideoError}");
Check(badPixels==0,$"pixel/order errors: {badPixels}");
Check(surroundChunks>0 && bgmChunks>0,$"real 7.1 BGM PCM was not observed: surround={surroundChunks} bgm={bgmChunks} audio={audioCount}");
Check(musicChanges.Any(e=>e.Kind=="pause") && musicChanges.Any(e=>e.Kind=="resume")
    && musicChanges.Any(e=>e.Kind=="seek") && musicChanges.Any(e=>e.Kind=="start"),"music control hooks missed commands");
Check(musicChanges.Count(e=>e.Kind=="parameter") is >0 and <20,"redundant parameter setters split continuous music");
Check(slow.DroppedFrames>0 && observer.CallbackErrors==0,"callback isolation failed");
Check(CaptureSource.SubscriberCount==0 && !CaptureSource.AudioAvailable,"last unsubscribe did not release audio");
foreach(var name in new[]{"first.mkv","second.mkv"}) {
    string path=Path.Combine(output,name);
    Check(File.Exists(path)&&new FileInfo(path).Length>1000,"missing video");
    Check(new FileInfo(path+".sfxchunks").Length>8,"missing FMOD PCM");
    Check(new FileInfo(path+".bgmchunks").Length>8,"missing independent BGM PCM");
    Check(File.ReadAllLines(path+".music.jsonl").Last().Contains("\"complete\":true"),"incomplete music journal");
}
NativeCaptureBridge.FinalizeRecordingAsync([new RecordingClip(Path.Combine(output,"first.mkv"),0,1.5,"",0)],
    Path.Combine(output,"final.mp4"),encoder,1000,30,false,false,"").GetAwaiter().GetResult();
NativeCaptureBridge.FinalizeRecordingAsync([new RecordingClip(Path.Combine(output,"first.mkv"),0,0.4,"",0),
    new RecordingClip(Path.Combine(output,"first.mkv"),0.8,0.4,"",0)],
    Path.Combine(output,"continuous-bgm.mp4"),encoder,1000,30,true,false,"").GetAwaiter().GetResult();
NativeCaptureBridge.FinalizeRecordingAsync([
    new RecordingClip(Path.Combine(output,"first.mkv"),0,0.3,"",0,true,false,"normal"),
    new RecordingClip(Path.Combine(output,"first.mkv"),0.5,0.3,"",0,true,true,"cassette"),
    new RecordingClip(Path.Combine(output,"first.mkv"),1.0,0.3,"",0,true,false,"normal")],
    Path.Combine(output,"room-bgm.mp4"),encoder,1000,30,true,false,"").GetAwaiter().GetResult();
// Non-keyframe cut through the public managed/native bridge. An unavailable
// encoder preference exercises the copy path, including real FMOD SFX,
// independent BGM and the music journal. Rust tests assert packet identity.
var replayTimer = System.Diagnostics.Stopwatch.StartNew();
NativeCaptureBridge.FinalizeRecordingAsync([
    new RecordingClip(Path.Combine(output,"second.mkv"),0.25,0.75,"",0,true,false,"normal"),
    new RecordingClip(Path.Combine(output,"second.mkv"),1.0,0.75,"",0,true,true,"cassette")],
    Path.Combine(output,"fast-death.mp4"),"mqol_test_no_such_encoder",1000,60,true,false,"",
    preferVideoCopy:true).GetAwaiter().GetResult();
Console.WriteLine($"Fast death replay finalization (1.5s): {replayTimer.Elapsed.TotalMilliseconds:F1} ms");
// Unhook and hook again while context remains alive (mod reload).
CaptureSource.Unload(); CaptureSource.Load();
using(var probe=NativeCaptureBridge.Start(60)) {
    for(int i=0;i<10;i++){Sdl.Swap(window);Thread.Sleep(20);}
    Check(probe.Statistics.FramesCaptured>0,"reload failed");
}
CaptureSource.Unload();
Sdl.DeleteContext(context);Sdl.DestroyWindow(window);
// Reject unsupported backends without preventing audio-only subscription or breaking SDL.
nint plain = Sdl.CreateWindow("Celeste non-GL integration",0,0,64,64,0x8);
Check(plain!=0,"plain SDL window failed");
CaptureSource.Load();
if(OperatingSystem.IsWindows()) {
    using(var waiting=CaptureSource.Subscribe(pixels:_=>{})) {
        CaptureSource.Update(); Thread.Sleep(5_100); CaptureSource.Update();
    }
}
Check(CaptureSource.VideoError is not null,"non-GL renderer was silently accepted");
bool rejected=false;
try { using var invalid=CaptureSource.Subscribe(pixels:_=>{}); } catch(NotSupportedException) { rejected=true; }
Check(rejected,"non-GL video subscription must fail");
using(var audioOnly=CaptureSource.Subscribe(fmod:_=>{})) { CaptureSource.Update(); Check(CaptureSource.AudioAvailable,"audio-only source requires video incorrectly"); }
CaptureSource.Unload();
Sdl.DestroyWindow(plain);Sdl.Quit();Fmod(studio.release());
File.WriteAllText(Path.Combine(output,"passed.txt"),$"frames={frameCount}, audio={audioCount}, slowDrops={slow.DroppedFrames}, pixelErrors={badPixels}");
Console.WriteLine($"PASS actual SDL native hooks/PBO/GL pixels, FMOD buses, two encoders, resize, unsubscribe, slow consumer, reload, finalizer: {frameCount} frames / {audioCount} audio");

internal static class Sdl {
    [DllImport("SDL2",EntryPoint="SDL_Init",CallingConvention=CallingConvention.Cdecl)] internal static extern int Init(uint flags);
    [DllImport("SDL2",EntryPoint="SDL_Quit",CallingConvention=CallingConvention.Cdecl)] internal static extern void Quit();
    [DllImport("SDL2",EntryPoint="SDL_GL_SetAttribute",CallingConvention=CallingConvention.Cdecl)] internal static extern int Attribute(int attr,int value);
    [DllImport("SDL2",EntryPoint="SDL_CreateWindow",CallingConvention=CallingConvention.Cdecl)] internal static extern nint CreateWindow([MarshalAs(UnmanagedType.LPUTF8Str)] string title,int x,int y,int w,int h,uint flags);
    [DllImport("SDL2",EntryPoint="SDL_DestroyWindow",CallingConvention=CallingConvention.Cdecl)] internal static extern void DestroyWindow(nint window);
    [DllImport("SDL2",EntryPoint="SDL_GL_CreateContext",CallingConvention=CallingConvention.Cdecl)] internal static extern nint CreateContext(nint window);
    [DllImport("SDL2",EntryPoint="SDL_GL_DeleteContext",CallingConvention=CallingConvention.Cdecl)] internal static extern void DeleteContext(nint context);
    [DllImport("SDL2",EntryPoint="SDL_GL_SwapWindow",CallingConvention=CallingConvention.Cdecl)] internal static extern void Swap(nint window);
    [DllImport("SDL2",EntryPoint="SDL_GL_SetSwapInterval",CallingConvention=CallingConvention.Cdecl)] internal static extern int SwapInterval(int interval);
    [DllImport("SDL2",EntryPoint="SDL_GL_GetProcAddress",CallingConvention=CallingConvention.Cdecl)] internal static extern nint GetProc([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport("SDL2",EntryPoint="SDL_GL_GetDrawableSize",CallingConvention=CallingConvention.Cdecl)] internal static extern void Drawable(nint window,out int w,out int h);
    [DllImport("SDL2",EntryPoint="SDL_SetWindowSize",CallingConvention=CallingConvention.Cdecl)] internal static extern void SetWindowSize(nint window,int w,int h);
    [DllImport("SDL2",EntryPoint="SDL_PumpEvents",CallingConvention=CallingConvention.Cdecl)] internal static extern void Pump();
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void ClearColor(float r,float g,float b,float a);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Clear(uint mask);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Enable(uint flag);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] internal delegate void Scissor(int x,int y,int w,int h);
}
namespace Celeste.Mod.MicroblocksQolUtils {
    internal enum LogLevel {Info,Warn,Error}
    internal static class Logger {
        internal static void Log(LogLevel level,string tag,string text)=>Console.WriteLine($"{level} {tag}: {text}");
        internal static void LogDetailed(Exception e,string tag)=>Console.WriteLine($"{tag}: {e}");
    }
}
