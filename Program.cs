using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Diagnostics;

namespace AireAPIExample
{
    class Program
    {
        //servers called
        private static string strMusicBrainURL = "http://musicbrainz.org/ws/2/";
        private static string strLyricsURL = "https://api.lyrics.ovh/v1/";
        private static readonly HttpClient httpArtist = new HttpClient();
        private static readonly HttpClient httpLyrics = new HttpClient();

        //page sizes for requests
        private static int intRecordingPageSize = 100;
        private static int intArtistPageSize = 25;

        //20 gives best balance of speed and successful calls on a standard broadband connection
        private static int intMaxConcurrency = 20;

        //queue pipeline for lyric counting
        private static ConcurrentQueue<Recording> recsLyricProcessingQueue;

        //collections for recordings
        private static List<Recording> recsUnique;
        private static List<Recording> recsLyricsFound;
        private static List<Recording> recsLyricsNotFound;
        private static List<Recording> recsLyricsCallFailed;

        //throttle to limit concurrency
        private static SemaphoreSlim semThrottler;
            
        //counters
        private static int intTotalRecordingCount;
        private static int intTotalLyricCount;
        private static int intSpinnerCounter = 0;
        private static Stopwatch watchElapsed;

        //status flags
        private static bool blCancelling;
        private static bool blInDisplay = false;

        static async Task Main(string[] args)
        {

            //set up the http client for the music brain apis
            httpArtist.DefaultRequestHeaders.Accept.Clear();
            httpArtist.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            httpArtist.DefaultRequestHeaders.Add("User-Agent", "AireAPIExample/1.0 (david.eagle@nhs.net)");

            //set up the http client for the lyric api
            httpLyrics.DefaultRequestHeaders.Accept.Clear();
            httpLyrics.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));
            httpLyrics.DefaultRequestHeaders.Add("User-Agent", "AireAPIExample/1.0 (david.eagle@nhs.net)");

            //prompt for artist name
            Console.Clear();
            Console.WriteLine("Aire Logic Technical Test ");
            Console.Write("Enter artist name (ENTER to exit): ");

            //and store
            string strArtist = Console.ReadLine();

            //if we got one...
            if (strArtist.Length > 0)
            {
                //default to no response
                ArtistSearchResponse asrInitalResponse = null;

                //prep for errors...
                try
                {
                    //...getting the artist list
                    asrInitalResponse = await SearchArtistByName(strArtist, 2, 0);
                }
                catch(Exception e)
                {

                }
                 
                //if we got a response...
                if(asrInitalResponse != null)
                {
                    //flag for whether we're still selecitng an artist
                    bool blSelecting = true;

                    //selected artist
                    Artist artSelected = null;

                    //initial offset
                    int intOffset = 0;

                    //while we're selecting...
                    while (blSelecting)
                    {
                        //get matches
                        ArtistSearchResponse asrResponse = await SearchArtistByName(strArtist, intArtistPageSize, intOffset);

                        //clear the console
                        Console.Clear();

                        //start a counter
                        int iCounter = intOffset;

                        //loop thro this page of artists
                        foreach (Artist artistEnum in asrResponse.Artists)
                        {
                            //increment
                            iCounter++;

                            //and list
                            Console.WriteLine("{0}. {1} ({2}) - relevance {3}%",
                                iCounter.ToString(), artistEnum.Name,
                                artistEnum.Country == null ? "no country specified" : artistEnum.Country,
                                artistEnum.Score.ToString());
                        }

                        //if we have artists...
                        if (asrResponse.Artists.Count > 0)
                        {
                            //prompt
                            Console.Write("\nEnter number of specific artist (M for more, P for previous, RETURN to exit): ");

                            //get the input
                            string strInput = Console.ReadLine();

                            //for more...
                            if (strInput.Equals("M", StringComparison.OrdinalIgnoreCase) && (intOffset + intArtistPageSize) < asrInitalResponse.Count)
                            {
                                //increase the offset
                                intOffset += intArtistPageSize;
                            }
                            else if (strInput.Equals("P", StringComparison.OrdinalIgnoreCase) && (intOffset - intArtistPageSize) >= 0)
                            {
                                //decrease the offset
                                intOffset -= intArtistPageSize;
                            }
                            else if (strInput.Equals(""))
                            {
                                //we're no longer selecting (exit)
                                blSelecting = false;
                            }
                            else
                            {
                                int intIndex = -1;
                                int.TryParse(strInput, out intIndex);

                                //if we have a valid selection...
                                if (intIndex > intOffset && intIndex <= intOffset + intArtistPageSize)
                                {
                                    //save the selected artist
                                    artSelected = asrResponse.Artists[intIndex - intOffset - 1];

                                    //flag we're no longer selecting
                                    blSelecting = false;
                                }
                            }

                        }
                        //no artists returned
                        else
                        {
                            //prompt
                            Console.Write("\nNo artists found with the name provided. ");

                            //flag we're not selecting
                            blSelecting = false;
                        }

                    }

                    //if we have a selected artist...
                    if (artSelected != null)
                    {
                        //process their lyrics
                        await ProcessArtist(artSelected);

                        //show final status
                        CompleteProgress();
                    }
                }
                //no artist list returned...
                else
                {
                    //alert the user
                    Console.WriteLine("\nUnable to retrieve any artists from the server. Please check your Internet connection and that the server is running.");
                }


            }

        }
        private static async Task ProcessArtist(Artist artCurrent)
        {
            //reset the cancelling flag
            blCancelling = false;

            //queue for lyrics to be processed
            recsLyricProcessingQueue = new ConcurrentQueue<Recording>();

            //create new lists for results
            recsUnique = new List<Recording>();
            recsLyricsFound = new List<Recording>();
            recsLyricsNotFound = new List<Recording>();
            recsLyricsCallFailed = new List<Recording>();

            //clear counters
            intTotalLyricCount = 0;
            intTotalRecordingCount = 0;

            //display the progress table
            DisplayProgress();

            //start a stopwatch
            watchElapsed = Stopwatch.StartNew();

            //settp a timer to update progress
            Timer timerDisplay = new Timer(UpdateProgress, null, 0, 100);

            //set up a cancel task, monitoring for [ENTER]
            Task taskCancel = Task.Run(() =>
            {
                //while enter isn't pressed...
                while (Console.ReadKey().Key != ConsoleKey.Enter)
                {
                    //yield
                    Task.Yield();
                }

                //flag we're cancelling
                blCancelling = true;
                
            });

            //set up  task to get unique recordings for the artist
            Task taskSearchUniqueRecordings = Task.Run(async () =>
            {
                await SearchUniqueRecordingsByArtist(artCurrent);
            });

            //set up a task to get lyrics for those recordings
            Task taskProcessRecordings = Task.Run(async () =>
            {
                await ProcessRecordings(artCurrent, taskSearchUniqueRecordings, intMaxConcurrency);
            });


            //run concurrently, or until the cancel task completes
            await Task.WhenAny(taskCancel, Task.WhenAll(taskSearchUniqueRecordings, taskProcessRecordings));

            //stop the stopwatch
            watchElapsed.Stop();

            //Dispose the timer
            timerDisplay.Dispose();

            


        }

        private static async Task SearchUniqueRecordingsByArtist(Artist artist)
        {

            //set up the call (browse:   /<RESULT_ENTITY_TYPE>?<BROWSING_ENTITY_TYPE>=<MBID>&limit=<LIMIT>&offset=<OFFSET>&inc=<INC>)
            UriBuilder uriSearch = new UriBuilder(strMusicBrainURL + "recording");
            NameValueCollection qvalsSearch = HttpUtility.ParseQueryString(uriSearch.Query);
            qvalsSearch["artist"] = artist.ID;
            qvalsSearch["limit"] = "2";
            uriSearch.Query = qvalsSearch.ToString();

            //get the streamed response
            Task<Stream> streamResponse = httpLyrics.GetStreamAsync(uriSearch.ToString());

            //and deserialise
            RecordingListBrowseResponse reclistInitial = await JsonSerializer.DeserializeAsync<RecordingListBrowseResponse>(await streamResponse);

            //store the intial count]
            intTotalRecordingCount = reclistInitial.Count;

            //loop thro all recorindings, page at a time
            for (int intPage = 0; (intPage <= intTotalRecordingCount / intRecordingPageSize) && !blCancelling; intPage++)
            {

                //set up the call (browse:   /<RESULT_ENTITY_TYPE>?<BROWSING_ENTITY_TYPE>=<MBID>&limit=<LIMIT>&offset=<OFFSET>&inc=<INC>)
                uriSearch = new UriBuilder(strMusicBrainURL + "recording");
                qvalsSearch = HttpUtility.ParseQueryString(uriSearch.Query);
                qvalsSearch["artist"] = artist.ID;
                qvalsSearch["limit"] = intRecordingPageSize.ToString();
                qvalsSearch["offset"] = Convert.ToString(intPage * intRecordingPageSize);
                uriSearch.Query = qvalsSearch.ToString();

                //get the streamed response
                streamResponse = httpLyrics.GetStreamAsync(uriSearch.ToString());

                //and deserialise
                RecordingListBrowseResponse reclistPage = await JsonSerializer.DeserializeAsync<RecordingListBrowseResponse>(await streamResponse);

                //loop thro'
                foreach (Recording recEnum in reclistPage.Recordings)
                {
                    //if this is unique...
                    if (!ContainsRecording(recsUnique, recEnum))
                    {
                        //add this
                        recsUnique.Add(recEnum);

                        //and queue
                        recsLyricProcessingQueue.Enqueue(recEnum);
                    }
                }

                //throttle to 1 request per second
                await Task.Delay(1000);

            }

        }

        private static async Task ProcessRecordings(Artist artist, Task taskSearchUniqueRecordings, int intConcurrencyLimit)
        {
            //list of tasks to process the lyric counts
            List<Task> tasks = new List<Task>();

            //use a semaphore to throttle our calls
            semThrottler = new SemaphoreSlim(intConcurrencyLimit);

            //set a matching connection limit
            ServicePointManager.FindServicePoint(new Uri(strLyricsURL)).ConnectionLimit = intConcurrencyLimit;

            //while we have items in the queue or we're still adding to it
            while((recsLyricProcessingQueue.Count > 0 || !taskSearchUniqueRecordings.IsCompleted) && !blCancelling)
            {
                //if we succeed in gettinh a recording...
                if (recsLyricProcessingQueue.TryDequeue(out Recording recEnum))
                {
                    
                    //await the throttle
                    await semThrottler.WaitAsync();

                    //if we haven't cancelled in the meantime...
                    if(!blCancelling)
                    {
                        //run a task to get the lyric count
                        tasks.Add(Task.Run(async () =>
                        {

                            //prep to receive status code
                            int intStatusCode = 0;

                            try
                            {
                                //get the response code from trying to count the lyrics
                                intStatusCode = await ProcessLyricCountForRecording(artist.Name, recEnum);
                            }
                            catch (Exception e)
                            {
                                //call error, record as 502
                                intStatusCode = 502;
                            }

                            //depending on the response code...
                            switch (intStatusCode)
                            {
                                case 200:

                                    //add to the lyrics found list
                                    recsLyricsFound.Add(recEnum);

                                    //add the number of lyrics
                                    intTotalLyricCount += recEnum.LyricCount;

                                    break;

                                default:

                                    //add to the failed list
                                    recsLyricsCallFailed.Add(recEnum);

                                    break;

                            }

                            //release this
                            semThrottler.Release();

                        }));
                    }
                    
                }
            }

            // await for all the tasks to complete
            await Task.WhenAll(tasks.ToArray());

        }

        private static void DisplayProgress()
        {
            //clear 
            Console.Clear();

            //show status
            Console.WriteLine("Processing average lyric count ");

            //show labels
            Console.WriteLine("\nTotal number of recordings found: ");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("Unique number of recordings found: ");

            Console.WriteLine("\nNumber of recordings with lyrics successfully retrieved: ");
            Console.WriteLine("Number of recordings with lyrics that could not be retrieved: ");
            Console.WriteLine("Recordings in queue for processing: ");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("Running average lyric count: ");

            Console.WriteLine("\nTime elapsed: ");
            Console.WriteLine("Estimated time remaining: ");
            Console.WriteLine("Overall Progress: ");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("Press ENTER to cancel at any time");
        }

        private static void CompleteProgress()
        {
            //clear 
            Console.Clear();

            //flag we're in display
            blInDisplay = true;

            //if we cancelled...
            if (blCancelling)
            {
                //update
                Console.WriteLine("Processing cancelled ");
            }
            else
            {
                //show completion
                Console.WriteLine("Processing complete ");
            }

            //show labels
            Console.WriteLine("\nTotal number of recordings found: ");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("Unique number of recordings found: ");

            Console.WriteLine("\nNumber of recordings with lyrics successfully retrieved: ");
            Console.WriteLine("Number of recordings with lyrics that could not be retrieved: ");
            Console.WriteLine(new string('=', 70));
            Console.WriteLine("Average lyric count: ");

            Console.WriteLine("\nTime elapsed: ");
            Console.WriteLine(new string('=', 70));

            //update value
            //update progress values (only on every 4th call - no need for quicker)
            Console.SetCursorPosition(65, 2);
            Console.Write(intTotalRecordingCount.ToString().PadLeft(5, ' '));
            Console.SetCursorPosition(65, 4);
            Console.Write(recsUnique.Count.ToString().PadLeft(5, ' '));
            Console.SetCursorPosition(65, 6);
            Console.Write(recsLyricsFound.Count.ToString().PadLeft(5, ' '));
            Console.SetCursorPosition(65, 7);
            Console.Write(Convert.ToString(recsLyricsNotFound.Count + recsLyricsCallFailed.Count).PadLeft(5, ' '));

            //if we have records...
            if (recsUnique.Count > 0 && (recsLyricsFound.Count + recsLyricsCallFailed.Count + recsLyricsNotFound.Count) > 0)
            {
                //if we have lyrics found...
                if (recsLyricsFound.Count > 0)
                {
                    Console.SetCursorPosition(65, 9);
                    Console.Write(Convert.ToString(intTotalLyricCount / recsLyricsFound.Count).PadLeft(5, ' '));
                }

                //get the time gone
                TimeSpan spanSecsPassed = watchElapsed.Elapsed;

                //add time and progress
                Console.SetCursorPosition(35, 11);
                Console.Write(ToReadableString(spanSecsPassed).PadLeft(35, ' '));
            }

            //and move down
            Console.SetCursorPosition(0, 14);

            //enable the cursor
            Console.CursorVisible = true;

            //flag we're out of display
            blInDisplay = false;

        }

        private static void UpdateProgress(object? state)
        {
            
            //ensure the cursor is off
            Console.CursorVisible = false;

            //if we're not cancelling or already in this routine...
            if(!blCancelling && !blInDisplay)
            {
                //flag we're here
                blInDisplay = true;

                //turn the spinner
                intSpinnerCounter += 1;

                //position for it
                Console.SetCursorPosition(32, 0);

                //and write
                switch (intSpinnerCounter % 4)
                {
                    case 0: Console.Write("/"); break;
                    case 1: Console.Write("-"); break;
                    case 2: Console.Write("\\"); break;
                    case 3:Console.Write("|");

                        //update progress values (only on every 4th call - no need for quicker)
                        Console.SetCursorPosition(65, 2);
                        Console.Write(intTotalRecordingCount.ToString().PadLeft(5, ' '));
                        Console.SetCursorPosition(65, 4);
                        Console.Write(recsUnique.Count.ToString().PadLeft(5, ' '));
                        Console.SetCursorPosition(65, 6);
                        Console.Write(recsLyricsFound.Count.ToString().PadLeft(5, ' '));
                        Console.SetCursorPosition(65, 7);
                        Console.Write(Convert.ToString(recsLyricsNotFound.Count + recsLyricsCallFailed.Count).PadLeft(5, ' '));
                        Console.SetCursorPosition(65, 8);
                        Console.Write(Convert.ToString(recsLyricProcessingQueue.Count).PadLeft(5, ' '));

                        //if we have records...
                        if (recsUnique.Count > 0 && (recsLyricsFound.Count + recsLyricsNotFound.Count + recsLyricsCallFailed.Count) > 0)
                        {
                            //if we have lyrics found...
                            if (recsLyricsFound.Count > 0)
                            {
                                Console.SetCursorPosition(65, 10);
                                Console.Write(Convert.ToString(intTotalLyricCount / recsLyricsFound.Count).PadLeft(5, ' '));
                            }

                            //get the progress fraction
                            double dblProgress = ((double)recsLyricsFound.Count + recsLyricsNotFound.Count + recsLyricsCallFailed.Count) / recsUnique.Count;

                            //get the time gone
                            TimeSpan spanSecsPassed = watchElapsed.Elapsed;

                            //estimate what's left
                            double dblSecsLeft = (spanSecsPassed.TotalSeconds * ((double)recsUnique.Count / (recsLyricsFound.Count + recsLyricsNotFound.Count + recsLyricsCallFailed.Count))) - spanSecsPassed.TotalSeconds;
                            TimeSpan spanSecsLeft = new TimeSpan(0, 0, (int)Math.Round(dblSecsLeft, 0));

                            //add time and progress
                            Console.SetCursorPosition(35, 12);
                            Console.Write(ToReadableString(spanSecsPassed).PadLeft(35, ' '));
                            Console.SetCursorPosition(35, 13);
                            Console.Write(ToReadableString(spanSecsLeft).PadLeft(35, ' '));
                            Console.SetCursorPosition(64, 14);
                            Console.Write(Convert.ToString(Math.Round(dblProgress * 100, 0)).PadLeft(5, ' ') + "%" + new string(' ', 20));


                        }
                        break;
                }
                
                //flag we're out
                blInDisplay = false;
            }
        }

        public static string ToReadableString(TimeSpan span)
        {
            //format, splitting into relevant units
            string strFormatted = string.Format("{0}{1}{2}{3}",
                span.Duration().Days > 0 ? string.Format("{0:0} day{1}, ", span.Days, span.Days == 1 ? string.Empty : "s") : string.Empty,
                span.Duration().Hours > 0 ? string.Format("{0:0} hour{1}, ", span.Hours, span.Hours == 1 ? string.Empty : "s") : string.Empty,
                span.Duration().Minutes > 0 ? string.Format("{0:0} minute{1}, ", span.Minutes, span.Minutes == 1 ? string.Empty : "s") : string.Empty,
                span.Duration().Seconds > 0 ? string.Format("{0:0} second{1}", span.Seconds, span.Seconds == 1 ? string.Empty : "s") : string.Empty);

            //remove trailing ,
            if (strFormatted.EndsWith(", ")) strFormatted = strFormatted.Substring(0, strFormatted.Length - 2);


            //handle 0
            if (string.IsNullOrEmpty(strFormatted)) strFormatted = "0 seconds";

            //and return
            return strFormatted;
        }

        private static bool ContainsRecording(List<Recording> recs, Recording recCurrent)
        {
            //loop thro the list
            foreach (Recording recEnum in recs)
            {
                //if our title's match...
                if (recEnum.Title.Equals(recCurrent.Title))
                {
                    //we contain the recording
                    return true;
                }
            }

            //otheriwse, we don't
            return false;
        }

        private static async Task<ArtistSearchResponse> SearchArtistByName(string strArtist, int intLimit, int intOffset)
        {
            //set up the call (search:   /<ENTITY_TYPE>?query=<QUERY>&limit=<LIMIT>&offset=<OFFSET>)
            UriBuilder uriSearch = new UriBuilder(strMusicBrainURL + "artist");
            NameValueCollection qvalsSearch = HttpUtility.ParseQueryString(uriSearch.Query);
            qvalsSearch["query"] = strArtist;
            qvalsSearch["limit"] = intLimit.ToString();
            qvalsSearch["offset"] = intOffset.ToString();
            uriSearch.Query = qvalsSearch.ToString();

            //get the streamed response
            Task<Stream> streamResponse = httpArtist.GetStreamAsync(uriSearch.ToString());

            //deserialise
            ArtistSearchResponse response = await JsonSerializer.DeserializeAsync<ArtistSearchResponse>(await streamResponse);

            //and return
            return response;
        }

        private static async Task<int> ProcessLyricCountForRecording(string strArtist, Recording recCurrent)
        {

            //set up the call
            UriBuilder uriSearch = new UriBuilder(strLyricsURL + strArtist + "/" + recCurrent.Title);

            //prep for retrieving the lyrics
            RecordingLyrics lyricsResponse;
  
            Task<Stream> streamResponse = null;

            int intLyricCount = -1;
            int intResponseCode = 0;

            //get a response from the api call
            using (var response = await httpLyrics.GetAsync(uriSearch.ToString(), HttpCompletionOption.ResponseHeadersRead))
            {
                //if we have a successful response...
                if (response.IsSuccessStatusCode)
                {
                    //ge the stream
                    streamResponse = response.Content.ReadAsStreamAsync();

                    //deserialise the lyrics
                    lyricsResponse = await JsonSerializer.DeserializeAsync<RecordingLyrics>(await streamResponse);

                    //and count them
                    intLyricCount = lyricsResponse.LyricCount();
                }
                
                //store the response code to return
                intResponseCode = (int)response.StatusCode;
            }

            //store the lyric count
            recCurrent.LyricCount = intLyricCount;

            //return the response code
            return intResponseCode;

        }
    }
}
