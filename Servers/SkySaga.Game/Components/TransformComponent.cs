using System;
using System.Numerics;

using RakNet;

namespace SkySaga.Game.Components;

public class TransformComponent : Component
{
    public Vector<int> Position { get; set { field = value; OnParameterChanged(); } }
    public float YawDegrees { get; set { field = value; OnParameterChanged(); } }
    public Vector3 Size { get; set { field = value; OnParameterChanged(); } }
    public float Scale { get; set { field = value; OnParameterChanged(); } }

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (parameterName.Equals(nameof(Position), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.WriteBits(BitConverter.GetBytes(Position[0]), 32 - Util.NumBitsRequiredUInt32(0x10000u), true);
            bitStream.WriteBits(BitConverter.GetBytes(Position[1]), 32 - Util.NumBitsRequiredUInt32(0x10000u), true);
            bitStream.WriteBits(BitConverter.GetBytes(Position[2]), 32 - Util.NumBitsRequiredUInt32(0x10000u), true);

            return true;
        }
        else if (parameterName.Equals(nameof(YawDegrees), StringComparison.OrdinalIgnoreCase))
        {
            // Deliberately NOT synced. Nothing on the server sets a meaningful yaw, and
            // including it is the difference between a Chest full-sync the client accepts and
            // one it chokes on. Returning false leaves the parameter's bit clear so the client
            // keeps its own value instead of receiving ours.
            return false;
        }
        else if (parameterName.Equals(nameof(Size), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.WriteBits(BitConverter.GetBytes(Size[0]), 32 - Util.NumBitsRequiredUInt32(9), true);
            bitStream.WriteBits(BitConverter.GetBytes(Size[1]), 32 - Util.NumBitsRequiredUInt32(9), true);
            bitStream.WriteBits(BitConverter.GetBytes(Size[2]), 32 - Util.NumBitsRequiredUInt32(9), true);

            return true;
        }
        else if (parameterName.Equals(nameof(Scale), StringComparison.OrdinalIgnoreCase))
        {
            bitStream.WriteBits(BitConverter.GetBytes(Scale), 32 - Util.NumBitsRequiredUInt32(56), true);

            return true;
        }

        return false;
    }
}