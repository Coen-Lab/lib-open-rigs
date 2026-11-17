using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bonsai.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenRigs.Logging
{
    public class JsonWriter : FileSink<string, StreamWriter>
    {
        protected override StreamWriter CreateWriter(string fileName, string input)
        {
            return new StreamWriter(fileName);
        }

        protected override void Write(StreamWriter writer, string input)
        {
            var parsed = JToken.Parse(input);
            var formatted = parsed.ToString(Formatting.Indented);
            writer.WriteLine(formatted);
            // Flushed at once: a settings file is one write, and a workflow that ends right after it must not lose it
            // (an empty Settings file on CI, when the write was still buffered at exit).
            writer.Flush();
        }
    }
}
