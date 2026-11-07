namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Global application configuration: identity, LLM, paths, and Elasticsearch connections.
    /// </summary>
    public class Config

    {
        /// <summary>Application name.</summary>
        public string appName { get; set; } = "APPx";
        /// <summary>Project or logical group (e.g. "common").</summary>
        public string? projectName { get; set; } = "common";
        /// <summary>Target language or locale ("*" for all).</summary>
        public string? language { get; set; } = "*";
        /// <summary>Optional functional category.</summary>
        public string? category { get; set; }
        /// <summary>Short application description.</summary>
        public string appDescription { get; set; } = "Description of the application ";
        /// <summary>URL of the associated documentation.</summary>
        public string appDocumentationUrl { get; set; } = @"https://www.kaonomi.net";
        /// <summary>Base URL of the Ollama server (e.g. http://localhost:11434).</summary>
        public string ollamaUrl { get; set; } = "http://localhost:11434";
        /// <summary>Base URL of the Fooocus API that generates the images (KAONOMI.Server/Fooocus/fooocus_api.py).</summary>
        public string fooocusUrl { get; set; } = "http://127.0.0.1:8888";
        /// <summary>Optional dialog server URL.</summary>
        public string? dialogServerUrl { get; set; }
        /// <summary>Root folder for documents on disk: by default the Documents folder of the user, on every system.</summary>
        public string? documentsRootFolder { get; set; } = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        /// <summary>Optional URL of a robot or external service.</summary>
        public string? robotUrl { get; set; }
        /// <summary>Elasticsearch settings for main data.</summary>
        public ElasticServerSettings? dataRepository { get; set; }
        /// <summary>Elasticsearch settings for dialog (otherwise reuse the main repository).</summary>
        public ElasticServerSettings? dialogRepository { get; set; }
        /// <summary>Elasticsearch settings for admin / logs (otherwise reuse the main repository).</summary>
        public ElasticServerSettings? adminRepository { get; set; }
        /// <summary>Default LLM model name (Ollama).</summary>
        public string llmmodel { get; set; } = "gemma3:4b";
        /// <summary>Target latency or threshold in milliseconds.</summary>
        public int latencyInms { get; set; } = 100;

        /// <summary>
        /// Folder of the NER model of the server (XLM-RoBERTa int8 in ONNX: model_quantized.onnx, tokenizer.json,
        /// config.json); null: <c>~/kaonomi/models/ner/xlm-roberta-base-ner-hrl-int8</c>.
        /// </summary>
        public string? nerModelFolder { get; set; }

        /// <summary>Look of the console applications: "matrix", "jarvis" or "ubuntu" applies that theme (HelperConsoleTheme), any other value keeps the classic console.</summary>
        public string consoleTheme { get; set; } = "matrix";
    }
}
