namespace KAONOMI.Core.Models
{
    /// <summary>
    /// Global application configuration: identity, LLM, paths, and Elasticsearch connections.
    /// </summary>
    public class Config

    {
        /// <summary>Application title.</summary>
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
        /// <summary>
        /// Base URL of the LLM server: Ollama (e.g. http://localhost:11434) or, with <see cref="llmProvider"/> set to
        /// "lmstudio", LM Studio (e.g. http://localhost:1234).
        /// </summary>
        public string ollamaUrl { get; set; } = "http://localhost:11434";
        /// <summary>
        /// Which API the server at <see cref="ollamaUrl"/> speaks: "ollama" (its own API, <c>/api/chat</c>) or
        /// "lmstudio" (the OpenAI-compatible API of LM Studio, <c>/v1/chat/completions</c>; "openai" is the same).
        /// </summary>
        public string llmProvider { get; set; } = "ollama";
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
        /// <summary>Default LLM model title (Ollama).</summary>
        public string llmmodel { get; set; } = "gemma3:4b";
        /// <summary>Target latency or threshold in milliseconds.</summary>
        public int latencyInms { get; set; } = 100;

        /// <summary>
        /// Folder of the NER model of the server (XLM-RoBERTa int8 in ONNX: model_quantized.onnx, tokenizer.json,
        /// config.json); null: <c>~/kaonomi/models/ner/xlm-roberta-large-finetuned-conll03-english-int8</c> (or the former
        /// <c>xlm-roberta-base-ner-hrl-int8</c> when the large one is missing).
        /// </summary>
        public string? nerModelFolder { get; set; }

        /// <summary>
        /// The most characters the processor of the dialogs sends to the model (the system prompt, the knowledge and
        /// the user prompt together): above, the previous exchanges written in the prompt are shortened, then the
        /// oldest dropped. About four characters make a token: 12000 fits a context of 4096 tokens with its answer.
        /// </summary>
        public int promptMaxChars { get; set; } = 12000;

        /// <summary>
        /// The most characters of knowledge (entities of the ontology, intels) the processor writes in the system
        /// prompt; 0: none is written.
        /// </summary>
        public int promptKnowledgeMaxChars { get; set; } = 3000;

        /// <summary>The last exchanges of a conversation the processor writes as they were said.</summary>
        public int contextRecentTurns { get; set; } = 2;

        /// <summary>The exchanges before those, shortened (300 characters of the question, 600 of the answer).</summary>
        public int contextShortTurns { get; set; } = 4;

        /// <summary>The exchanges before those, brief (160 and 220 characters); the older ones become digests (100 and 120).</summary>
        public int contextBriefTurns { get; set; } = 8;

        /// <summary>The most characters of the context of the conversation written in a prompt; above, the oldest exchanges are left out.</summary>
        public int contextMaxChars { get; set; } = 6000;

        /// <summary>Look of the console applications: "matrix", "jarvis" or "ubuntu" applies that theme (HelperConsoleTheme), any other value keeps the classic console.</summary>
        public string consoleTheme { get; set; } = "matrix";
    }
}
