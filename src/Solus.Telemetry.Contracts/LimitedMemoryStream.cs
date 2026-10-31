namespace Solus.Telemetry.Contracts;

// Buffers stay private; callers receive a complete admitted value or no value.
internal sealed class LimitedMemoryStream(int maximum, TelemetryDiagnostic diagnostic) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count) { Admit(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Admit(buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Admit(1); base.WriteByte(value); }
    private void Admit(int count)
    {
        if (Length + count > maximum) throw new AdmissionException(diagnostic);
    }
}
