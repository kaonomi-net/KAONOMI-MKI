using KAONOMI.Admin.Models;
using KAONOMI.Core;
using KAONOMI.Core.Helpers;
using KAONOMI.Core.Models;
using System.Text.Json;

namespace KAONOMI.Admin
{
    /// <summary>
    /// Admin console: loads config, logs to Elasticsearch, REPL loop toward Ollama.
    /// </summary>
    internal class Program
    {
        static private SessionManager _session;
        static private Config _config;
        static private string _defaultModel;

        /// <summary>Entry point: configuration, session, input loop, and async LLM calls.</summary>
        /// <param title="args">Command-line arguments (unused).</param>
        static async Task Main(string[] args)
        {
            string ollamaUrl = "http://localhost:11434";
            string defaultModel = "mistral-small3.2";
            _defaultModel = defaultModel;

            // Load configuration
            Config config = HelperConfigManager.GetFromFile<Config>("config.json");
            _config = config;
            // The LLM server and the model of config.json (ollamaUrl, llmProvider: "ollama" or "lmstudio", llmmodel).
            if (!string.IsNullOrWhiteSpace(config.ollamaUrl))
                ollamaUrl = config.ollamaUrl;
            if (!string.IsNullOrWhiteSpace(config.llmmodel))
                defaultModel = config.llmmodel;
            _defaultModel = defaultModel;
            string AppName = config.appName;

            HelperConsoleTheme.Start(config.appName, config.appDescription, config.appDocumentationUrl, config.consoleTheme);

            _session = new SessionManager(_config);
            _session.LogSave("Application started", AppName, "Info");

            string input = "";
            string path = "root/";

            while (true)
            {
                // UI prompt handling
                HelperConsoleTheme.Prompt(path);

                input = Console.ReadLine();

                // Exit conditions
                if (string.IsNullOrEmpty(input) || input == "/bye" || input == "quit()" || input == "/exit" || input == "/quit")
                {
                    HelperConsoleTheme.TypeLine(HelperConsoleTheme.Farewell, delayMs: 20);
                    break;
                }
                else if (string.IsNullOrEmpty(input) || input == "/rocstory" )
                {
                    HelperConsoleTheme.Info("Launch");
                   // LoadStoriesFromDirectoryAsync(@"F:\Data\Isotopies\ROCSTORIES-CHOCOLATINEV1214BQ6K", "chocolatine14b" ,"RocStory" , false);

                }
                else if (string.IsNullOrEmpty(input) || input == "/model" || input == "/Model" )
                {
                    HelperConsoleTheme.Info("Selected model : " + _defaultModel);
                    
                }
                else
                {
                    await OllamaCall(ollamaUrl, defaultModel, AppName, input);

                }
                Console.WriteLine(); // Blank line for readability
            }

            _session.LogSave("Application stopped", AppName, "Info");
        }

        /// <summary>Sends the prompt to Ollama, displays the response, persists logs / perf / completion.</summary>
        private static async Task OllamaCall(string ollamaUrl, string defaultModel, string AppName, string input)
        {
            try
            {
                Performance perf = new Performance();
                perf.date = DateTime.Now;
                perf.comment = "LLM Call Performance using " + _defaultModel + ".";
                perf.machineName = Environment.MachineName;
                perf.userName = Environment.UserName;
                perf.appName = _config.appName;
                perf.operationName = "LLM Call";
                perf.startTime = perf.date;
                perf.metadatas = new List<MetaData>();
                MetaData nxMetaData = new MetaData();
                nxMetaData.Key = "Model";
                nxMetaData.Value_String = defaultModel;
                perf.metadatas.Add(nxMetaData);
                MetaData nxMetaData2 = new MetaData();
                nxMetaData2.Key = "InputLength";
                nxMetaData2.Value_Int = input.Length;
                perf.metadatas.Add(nxMetaData2);

                var result = await HelperOllamaClient.SendChatMessageAsync(ollamaUrl, defaultModel, input, null, _config.llmProvider);

                // Display results
                Console.WriteLine();
                HelperConsoleTheme.TypeLine($"AI: {result.Content}", delayMs: 2);
                HelperConsoleTheme.Trace($"[Stats: {result.ResponseTokens} tokens | {result.TokensPerSecond} tk/s | {result.TotalDurationSec}s]");

                // Save log with the actual response content
                _session.LogSave($"User input: {input} | Response: {result.Content}", AppName, "Info");

                MetaData nxMetaData3 = new MetaData();
                nxMetaData3.Key = "OutputLength";
                nxMetaData3.Value_Int = result.Content.Length;
                perf.metadatas.Add(nxMetaData3);

                MetaData nxMetaData4 = new MetaData();
                nxMetaData4.Key = "ResponseTokens";
                nxMetaData4.Value_Int = result.ResponseTokens;
                perf.metadatas.Add(nxMetaData4);

                MetaData nxMetaData5 = new MetaData();
                nxMetaData5.Key = "LmmProcessCompletionInMs";
                nxMetaData5.Value_Double = result.TotalDurationSec;
                perf.metadatas.Add(nxMetaData5);


                perf.endTime = DateTime.Now;
                perf.durationMilliseconds = (int)(perf.endTime - perf.startTime).TotalMilliseconds;
                _session.PerformanceSave(perf);

                Completion nxCompletion = new Completion();
                nxCompletion.prompt = input;
                nxCompletion.completion = result.Content;
                nxCompletion.model = _config.llmmodel;
                nxCompletion.metadatas = new List<MetaData>();
                nxCompletion.date = DateTime.Now;
                nxCompletion.userName = Environment.UserName;
                nxCompletion.machineName = Environment.MachineName;
                nxCompletion.appName = _config.appName;
                nxCompletion.comment = "LLM Completion using " + defaultModel + ".";
                nxCompletion.collectionName = "user";

                _session.CompletionSave(nxCompletion);
            }
            catch (Exception ex)
            {
                Console.WriteLine();
                HelperConsoleTheme.Error(ex.Message);
                _session.LogSave($"Error during Ollama call: {ex.Message}", AppName, "Error");
            }
        }

        
       
    }
}
