using System.Reflection;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Library;
using VRCFaceTracking.Core.Types;

namespace ETVRTrackingModule
{
    public class ETVRTrackingModule : ExtTrackingModule
    {
        private OSCManager? _oscManager;
        private OSCSteamLink? _oscSteamLink;
        private ExpressionsMapperManager? _expressionMapper;
        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, false);
        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation.Name = "ETVR (Steam Link gaze) Eye Tracking module";
            var stream = GetType().Assembly.GetManifestResourceStream("ETVRTrackingModule.Assets.ETVRLogo.png");
            ModuleInformation.StaticImages = stream != null? new List<Stream> { stream } : ModuleInformation.StaticImages;

            var currentPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            ETVRConfigManager configManager = new ETVRConfigManager(currentPath, Logger);
            configManager.LoadConfig();
            
            _expressionMapper = new ExpressionsMapperManager(Logger, configManager.Config);
            _expressionMapper.RegisterSelf(ref configManager);
            
            _oscManager = new OSCManager(Logger, _expressionMapper);
            _oscManager.RegisterSelf(ref configManager);
            _oscManager.Start();

            _oscSteamLink = new OSCSteamLink(Logger);
            _oscSteamLink.RegisterSelf(ref configManager);
            _oscSteamLink.Start();

            if (_oscManager.State == OSCState.CONNECTED) return (true, false);
            
            Logger.LogError("ETVR Module could not connect to the specified port.");
            return (false, false);
        }

        public override void Teardown()
        {
            _oscManager?.TearDown();
            _oscSteamLink?.TearDown();
        }

        public override void Update()
        {
            // ETVR still drives openness, brows etc., but gaze comes from Steam Link while it's sending data
            Vector2? steamLinkGaze = _oscSteamLink != null && _oscSteamLink.TryGetGaze(out var gaze) ? gaze : null;
            _expressionMapper!.UpdateVRCFTState(steamLinkGaze);
            Thread.Sleep(5);
        }
    }
}