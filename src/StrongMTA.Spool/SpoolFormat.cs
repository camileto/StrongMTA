namespace StrongMTA.Spool;

internal static class SpoolFormat
{
    public static readonly byte[] MsgMagic = "SMTAMSG1"u8.ToArray();
}
