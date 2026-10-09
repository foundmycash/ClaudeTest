using UnityEngine.XR;

namespace LoadingScreen
{
    /// <summary>Short controller rumbles for the loading cinematic. Does nothing when no XR controllers are present.</summary>
    public static class LoadingHaptics
    {
        public static void Pulse(float amplitude, float duration)
        {
            Send(XRNode.LeftHand, amplitude, duration);
            Send(XRNode.RightHand, amplitude, duration);
        }

        static void Send(XRNode node, float amplitude, float duration)
        {
            InputDevice device = InputDevices.GetDeviceAtXRNode(node);
            if (device.isValid && device.TryGetHapticCapabilities(out HapticCapabilities caps) && caps.supportsImpulse)
                device.SendHapticImpulse(0, amplitude, duration);
        }
    }
}
