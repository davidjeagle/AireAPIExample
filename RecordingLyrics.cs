using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace AireAPIExample
{
    class RecordingLyrics
    {
        [JsonPropertyName("lyrics")]
        public string Lyrics { get; set; }

        public int LyricCount()
        {

            //default to -1 for an error
            int intCount = -1;

            //set the delimiters
            char[] delimiters = new char[] { ' ', '\r', '\n', '\t' };

            try
            {
                //split based on delimiters, removing empty strings
                intCount = this.Lyrics.Split(delimiters, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            catch (Exception e)
            { }

            //return the count
            return intCount;
        }
    }
}
