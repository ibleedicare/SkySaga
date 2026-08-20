using System;
using System.Collections.Generic;

using RakNet;

using SkySaga.Game.Extensions;

namespace SkySaga.Game.Components;

/// <summary>
/// One message in the player's inbox — the `0x70`-byte client struct, minus the fields the
/// mailbox UI never reads.
/// </summary>
/// <remarks>
/// Field order and widths come from the client's deserialiser <c>FUN_008cde80</c>, transcribed
/// in documentations/mail.md §3. The two optional records (item-gift template, job/adventure
/// template) are deliberately never written: with both absent bits clear the client takes the
/// third branch of <c>FUN_007c68b0</c>, which renders <see cref="Subject"/> and <see cref="Body"/>
/// verbatim and shows the sender as the literal "SkySaga". Those records exist so the real
/// server could send localised templated mail; an emulator has no use for them.
/// </remarks>
public class MailItem
{
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;

    /// <summary>
    /// The third string. The client reads it and the mailbox UI never uses it (mail.md
    /// "Unresolved"), so it goes out empty — but it must be written, or the stream desyncs.
    /// </summary>
    public string Unknown { get; set; } = string.Empty;

    /// <summary>Unix milliseconds. 64 bits; the epoch and unit are an inference (mail.md §7).</summary>
    public ulong Timestamp { get; set; }

    /// <summary>The key every client → server mail packet sends back.</summary>
    public string MessageUuid { get; set; } = string.Empty;

    /// <summary>
    /// The <c>MailItem</c> entity holding this mail's attachments, or 0 for none. It must reach
    /// the client (as an <c>EntityAdd</c>) before a sync references its id.
    /// </summary>
    public int AttachmentEntity { get; set; }

    /// <summary>Substitution arguments for templated text. Unused here; ≤ 5 on the wire.</summary>
    public List<string> TextArguments { get; set; } = [];

    /// <summary>
    /// Bit 0 read · bit 2 offers a gift choice · bit 3 gift chosen. Bit 1 is read by the client
    /// and its meaning is unknown. Set by <c>MailRead</c> / <c>MailGiftSelected</c>, which flip
    /// the client's own copy before sending — so a re-sync with a bit clear un-does what the
    /// player just did.
    /// </summary>
    public byte Flags { get; set; }

    public bool IsRead
    {
        get => (Flags & 1 << ReadBit) != 0;
        set => Flags = (byte)(value ? Flags | 1 << ReadBit : Flags & ~(1 << ReadBit));
    }

    public bool GiftChosen
    {
        get => (Flags & 1 << GiftChosenBit) != 0;
        set => Flags = (byte)(value ? Flags | 1 << GiftChosenBit : Flags & ~(1 << GiftChosenBit));
    }

    private const int ReadBit = 0;
    private const int GiftChosenBit = 3;
}

/// <summary>
/// The player's inbox. One synced parameter, <c>mailitemlist</c> — syncindex <b>50</b> on
/// <c>Player</c>.
/// </summary>
/// <remarks>
/// Syncing this list is also what lights the HUD mail icon: the client derives its own "total"
/// and "unclaimed attachment" counts from the list and raises UI events <c>0x59</c>/<c>0x5a</c>.
/// No notification packet is involved in that.
///
/// It is **not** what makes the inbox panel stop saying "loading" — that needs
/// <c>RemoteMailSynced</c>; see <c>Packets/MailCheck.cs</c>.
/// </remarks>
public class MailBoxComponent : Component
{
    /// <summary>Client-side default for the count-optimised list (mail.md §3).</summary>
    private const int MailItemListDefaultCount = 64;

    /// <summary>Default for each mail's text-argument list.</summary>
    private const int TextArgumentsDefaultCount = 5;

    public List<MailItem> MailItemList { get; set { field = value; OnParameterChanged(); } } = [];

    /// <summary>
    /// Raise the change for <see cref="MailItemList"/> without replacing it — for edits made
    /// through the items themselves (marking one read, taking an attachment), which the list's
    /// own setter cannot see.
    /// </summary>
    public void MarkChanged() => OnParameterChanged(nameof(MailItemList));

    public override bool TrySync(string parameterName, BitStream bitStream)
    {
        if (!parameterName.Equals(nameof(MailItemList), StringComparison.OrdinalIgnoreCase))
            return false;

        WriteCount(bitStream, MailItemList.Count, MailItemListDefaultCount);

        foreach (var mail in MailItemList)
        {
            bitStream.WriteString(mail.Subject);
            bitStream.WriteString(mail.Body);
            bitStream.WriteString(mail.Unknown);

            bitStream.WriteUInt64(mail.Timestamp);

            bitStream.WriteString(mail.MessageUuid);

            bitStream.Write(mail.AttachmentEntity);

            bitStream.Write(mail.Flags);

            WriteCount(bitStream, mail.TextArguments.Count, TextArgumentsDefaultCount);

            foreach (var argument in mail.TextArguments)
                bitStream.WriteString(argument);

            // recordA_present / recordB_present — see the MailItem remarks.
            bitStream.Write0();
            bitStream.Write0();
        }

        return true;
    }

    /// <summary>
    /// The count-optimised list header: the count in <c>NumBitsRequired(default)</c> bits, and
    /// an escape to a full 32-bit count when it reaches the default.
    /// </summary>
    /// <remarks>
    /// Byte-for-byte the encoding <see cref="InventoryComponent"/> writes for
    /// <c>inventoryentitylist</c>; only the default differs (64 for the mail list, 5 for a
    /// mail's text arguments, 45 there).
    /// </remarks>
    private static void WriteCount(BitStream bitStream, int count, int defaultCount)
    {
        var width = 32 - Util.NumBitsRequiredUInt32((uint)defaultCount);

        bitStream.WriteBits(BitConverter.GetBytes(Math.Min(count, defaultCount)), width, true);

        if (count < defaultCount)
            return;

        bitStream.Write1();
        bitStream.Write(count);
    }
}
