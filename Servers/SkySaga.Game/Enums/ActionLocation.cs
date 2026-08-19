namespace SkySaga.Game.Enums;

/// <summary>
/// Which equipment slot performed a voxel action. These are the same indices as the
/// inventory's equipment squares (see the slot map on <c>Connection</c>), which is what
/// tells the server whether a hand was holding a block.
/// </summary>
/// <remarks>
/// This field was previously modelled as a "BlockLocation" (face / inside / edge / corner),
/// which was wrong: idkb8907/SkySaga_Server reads it as the acting slot, and that matches our
/// own equipment layout exactly.
/// </remarks>
public enum ActionLocation
{
    LeftHand = 0,
    RightHand = 1,
    Head = 2,
    Torso = 3,
    Legs = 4,
    Arms = 5
}
