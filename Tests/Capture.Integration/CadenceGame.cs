using System.Diagnostics;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Celeste.Mod.MicroblocksQolUtils;

// Real FNA Game/SDL/DXGI presentation, with production managed callback workers.
// Never launches Celeste/Steam or touches the user's settings and saves.
internal sealed class CadenceGame : Game {
    private const int Width=2560,Height=1506,RenderFps=120;
    private readonly int totalDraws=RenderFps*int.Parse(Environment.GetEnvironmentVariable("MQOL_TEST_SECONDS")??"20");
    private readonly GraphicsDeviceManager graphics;
    private readonly string root,output,encoder;
    private Texture2D picture=null!,white=null!;
    private SpriteBatch sprites=null!;
    private FMOD.Studio.System studio=null!;
    private NativeCaptureSession capture=null!;
    private NativeCaptureSession? second;
    private Task? stopping;
    private CaptureStatistics result;
    private CaptureDeliveryStatistics delivery;
    private readonly Stopwatch watch=new();
    private int draws;
    internal CadenceGame(string root,string output,string encoder) {
        this.root=root; this.output=output; this.encoder=encoder;
        IsFixedTimeStep=false;
        graphics=new GraphicsDeviceManager(this) { PreferredBackBufferWidth=Width,
            PreferredBackBufferHeight=Height,SynchronizeWithVerticalRetrace=false };
        Window.Title="Celeste full-resolution cadence test";
    }
    protected override void LoadContent() {
        byte[] sample=File.ReadAllBytes(Environment.GetEnvironmentVariable("MQOL_TEST_BGRA")!);
        if(sample.Length!=Width*Height*4) throw new Exception("wrong BGRA fixture dimensions");
        for(int i=0;i<sample.Length;i+=4) (sample[i],sample[i+2])=(sample[i+2],sample[i]);
        picture=new Texture2D(GraphicsDevice,Width,Height); picture.SetData(sample);
        white=new Texture2D(GraphicsDevice,1,1); white.SetData(new[]{Color.White});
        sprites=new SpriteBatch(GraphicsDevice);
        static void Fmod(FMOD.RESULT result) { if(result!=FMOD.RESULT.OK) throw new Exception($"FMOD {result}"); }
        Fmod(FMOD.Studio.System.create(out studio));
        Fmod(studio.initialize(128,FMOD.Studio.INITFLAGS.NORMAL,FMOD.INITFLAGS.NORMAL,0));
        foreach(string bank in new[]{"Master Bank.bank","Master Bank.strings.bank","ui.bank","music.bank","sfx.bank"})
            Fmod(studio.loadBankFile(Path.Combine(root,"Content","FMOD","Desktop",bank),FMOD.Studio.LOAD_BANK_FLAGS.NORMAL,out _));
        typeof(Celeste.Audio).GetField("system",BindingFlags.Static|BindingFlags.NonPublic)!.SetValue(null,studio);
        Fmod(studio.getEvent("event:/music/lvl1/main",out var music)); Fmod(music.createInstance(out var song)); Fmod(song.start());
        NativeCaptureBridge.Initialize(null); CaptureSource.Load();
        capture=NativeCaptureBridge.StartRecording(60,Path.Combine(output,"fna-d3d.mkv"),encoder,12000);
        if(Environment.GetEnvironmentVariable("MQOL_TEST_DUAL")=="1")
            second=NativeCaptureBridge.StartRecording(60,Path.Combine(output,"fna-second.mkv"),encoder,12000);
        watch.Start();
    }
    protected override void Update(GameTime time) {
        CaptureSource.Update(); studio.update();
        if(stopping?.IsCompleted==true) { stopping.GetAwaiter().GetResult(); Exit(); }
    }
    protected override void Draw(GameTime time) {
        double due=(double)draws/RenderFps;
        while(watch.Elapsed.TotalSeconds<due) {
            if(due-watch.Elapsed.TotalSeconds>.002) Thread.Sleep(1); else Thread.SpinWait(20);
        }
        GraphicsDevice.Clear(Color.Black);
        sprites.Begin(SpriteSortMode.Deferred,BlendState.Opaque,SamplerState.PointClamp,DepthStencilState.None,RasterizerState.CullNone);
        // Scrolling defeats a static-picture-only compression benchmark.
        int scroll=Environment.GetEnvironmentVariable("MQOL_TEST_SCROLL")=="1" ? (draws*2)%Width : 0;
        sprites.Draw(picture,new Rectangle(-scroll,0,Width,Height),Color.White);
        if(scroll!=0) sprites.Draw(picture,new Rectangle(Width-scroll,0,Width,Height),Color.White);
        for(int bit=0;bit<16;bit++) sprites.Draw(white,new Rectangle(bit*32,0,32,32),((draws>>bit)&1)==0?Color.Black:Color.White);
        sprites.End(); draws++;
        if(draws%240==0) Console.WriteLine($"FNA wall={watch.Elapsed.TotalSeconds:F2} presents={draws} backend={CaptureSource.VideoBackend} {capture.Statistics} {capture.DeliveryStatistics}");
        if(draws==totalDraws) {
            double elapsed=watch.Elapsed.TotalSeconds;
            stopping=Task.Run(()=> {
                capture.Stop(); result=capture.Statistics; delivery=capture.DeliveryStatistics;
                second?.Stop();
                string report=$"wall={elapsed:F3} backend={CaptureSource.VideoBackend} sourcePoolDrops={CaptureSource.DroppedFrames} sourceAudioDrops={CaptureSource.DroppedAudioChunks}\n{result}\n{delivery}";
                Console.WriteLine("FNA_FINAL "+report);
                File.WriteAllText(Path.Combine(output,"report.txt"),report);
                if(second is not null) {
                    var stats=second.Statistics; var drops=second.DeliveryStatistics;
                    string extra=$"\nSECOND {stats}\n{drops}";
                    Console.WriteLine(extra); File.AppendAllText(Path.Combine(output,"report.txt"),extra);
                    if(stats.FramesCaptured<stats.MediaTimeSeconds*60*.995 || stats.FramesConsumed!=stats.FramesCaptured
                        || stats.FramesDropped!=0 || drops.DroppedFrames!=0 || drops.CallbackErrors!=0
                        || stats.AudioChunksDropped!=0 || drops.DroppedAudioChunks!=0 || drops.DroppedMusicEvents!=0)
                        throw new Exception("Second full-resolution recording cadence failed");
                }
                if(elapsed>(double)totalDraws/RenderFps+1 || result.FramesCaptured<result.MediaTimeSeconds*60*.995
                    || result.FramesDropped!=0 || result.FramesConsumed!=result.FramesCaptured
                    || delivery.DroppedFrames!=0 || delivery.CallbackErrors!=0 || result.AudioChunksDropped!=0
                    || delivery.DroppedAudioChunks!=0 || delivery.DroppedMusicEvents!=0
                    || CaptureSource.DroppedFrames!=0 || CaptureSource.DroppedAudioChunks!=0)
                    throw new Exception("FNA full-resolution recording cadence failed");
                File.WriteAllText(Path.Combine(output,"passed.txt"),report);
            });
        }
    }
    protected override void UnloadContent() {
        capture?.Dispose(); second?.Dispose(); CaptureSource.Unload(); studio.release();
        sprites?.Dispose(); picture?.Dispose(); white?.Dispose();
    }
}
