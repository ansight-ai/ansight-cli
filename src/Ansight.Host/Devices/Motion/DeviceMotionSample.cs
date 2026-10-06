using Ansight.Adb;

namespace Ansight.Host.Devices.Motion;

internal readonly record struct DeviceMotionSample(EmulatorAcceleration Acceleration, int HoldMilliseconds);
