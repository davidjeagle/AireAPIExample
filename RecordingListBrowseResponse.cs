using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace AireAPIExample
{
    class RecordingListBrowseResponse
    {
        [JsonPropertyName("recording-count")]
        public int Count { get; set; }

        [JsonPropertyName("recordings")]
        public List<Recording> Recordings { get; set; }
    }
}
