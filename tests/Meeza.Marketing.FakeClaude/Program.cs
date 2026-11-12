using System.Text;
using System.Text.Json;

// Behaves like `claude -p --output-format json`: reads the prompt from stdin and prints one JSON result.
// The "result" text is a JSON echo of the exact argv and stdin received, so a test can compare them byte for byte.
Console.InputEncoding = new UTF8Encoding(false);
var stdin = Console.In.ReadToEnd();
var echo = JsonSerializer.Serialize(new { argv = args, stdin });
var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
stdout.Write(JsonSerializer.Serialize(new
{
    type = "result", subtype = "success", is_error = false, result = echo, stop_reason = "end_turn",
    modelUsage = new Dictionary<string, object> { ["fake-model"] = new { } },
}));
stdout.Flush();
