using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace GitDotNet.Console.Tools;
internal class Utf8JsonlWriter : IDisposable
{
    private readonly StreamWriter _textWriter;
    private long _jsonlPosition;

    public Utf8JsonlWriter(Stream stream)
    {
        JsonWriter = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        _textWriter = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
        {
            NewLine = "\n"
        };
    }

    public Utf8JsonWriter JsonWriter { get; }

    public void StartNewJsonlObject()
    {
        JsonWriter.Flush();
        JsonWriter.Reset();

        _textWriter.WriteLine();
        _textWriter.Flush();

        _jsonlPosition = _textWriter.BaseStream.Position;
    }

    public void RollbackJsonlObject()
    {
        JsonWriter.Flush();
        JsonWriter.Reset();

        _textWriter.BaseStream.Position = _jsonlPosition;
    }

    public void Dispose()
    {
        JsonWriter.Dispose();
        _textWriter.Dispose();
    }
}
