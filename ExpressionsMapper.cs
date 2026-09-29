using ETVRTrackingModule.ExpressionStrategies;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Types;

namespace ETVRTrackingModule
{
    public class ExpressionsMapperManager
    {
        private V1Mapper _v1Mapper;
        private V2Mapper _v2Mapper;
        private BaseParamMapper _currentMapper;
        private readonly UnifiedEyeData _eyeData = new();

        ILogger _logger;
        public ExpressionsMapperManager(ILogger logger, Config config)
        {
            _logger = logger;
            _v1Mapper = new V1Mapper(_logger, config);
            _v2Mapper = new V2Mapper(_logger, config);
            _currentMapper = _v1Mapper;
        }

        public void RegisterSelf(ref ETVRConfigManager configManager)
        {
            configManager.RegisterListener(HandleConfigUpdate);
        }

        private void HandleConfigUpdate(Config config)
        {
            _v1Mapper.UpdateConfig(config);
            _v2Mapper.UpdateConfig(config);
        }
        
        public void MapMessage(OSCMessage msg)
        {
            if (!msg.success)
                return;

            if (IsV2Param(msg))
            {
                _currentMapper = _v2Mapper;
                _v2Mapper.HandleOSCMessage(msg);
                return;
            }

            _currentMapper = _v1Mapper;
            _v1Mapper.HandleOSCMessage(msg);
        }

        private bool IsV2Param(OSCMessage oscMessage)
        {
            var isv2Param = oscMessage.address.Contains("/v2/");
            return isv2Param;
        }

        public void UpdateVRCFTState(Vector2? gazeOverride)
        {
            // VRCFT reads UnifiedTracking.Data from its own thread, so we build the eye data here first
            // and publish it once. Otherwise it can catch ETVR's gaze before the override replaces it
            var eyeData = _eyeData;
            _currentMapper.UpdateVRCFTEyeData(ref eyeData, ref UnifiedTracking.Data.Shapes);

            if (gazeOverride is { } gaze)
            {
                eyeData.Left.Gaze = gaze;
                eyeData.Right.Gaze = gaze;
            }

            UnifiedTracking.Data.Eye.Left = eyeData.Left;
            UnifiedTracking.Data.Eye.Right = eyeData.Right;
        }
    }
}