using Avantra.AIAdvisory.Domain.Knowledge;

namespace AvantraAgent.Knowledge;

/// <summary>Knowledge bases created at first start if they do not exist yet (more can be added at runtime via the API).</summary>
public sealed class KnowledgeBaseSeedOptions
{
    public const string Section = "KnowledgeBases";
    public List<KnowledgeBaseSeed> Seed { get; set; } = [];
}

public sealed class KnowledgeBaseSeed
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? IndexName { get; set; }
    public string? SourceFolder { get; set; }
    public double SyncIntervalHours { get; set; }
    /// <summary>Optional website crawl (sitemaps, include/exclude paths, limits).</summary>
    public CrawlConfig? Crawl { get; set; }

    public KnowledgeBaseDefinition ToDefinition() => new()
    {
        Code = Code, Name = Name, Description = Description, IndexName = IndexName ?? "", SourceFolder = SourceFolder, SyncIntervalHours = SyncIntervalHours,
        CrawlConfigJson = Crawl is null ? null : System.Text.Json.JsonSerializer.Serialize(Crawl, CrawlJson.Options),
    };
}

public sealed class IngestionOptions
{
    public const string Section = "Ingestion";
    /// <summary>Where uploaded files wait until the background worker has ingested them.</summary>
    public string StagingPath { get; set; } = "data/staging";
    /// <summary>Folders that knowledge bases may sync from (relative paths resolve upwards from the app folder).</summary>
    public string AllowedSourceRoot { get; set; } = "knowledge";
    /// <summary>Hosts that documents may be downloaded from (https only). Empty = URL ingestion disabled.</summary>
    public List<string> AllowedUrlHosts { get; set; } = [];
    public int MaxFileSizeMb { get; set; } = 25;
    public int MaxFilesPerUpload { get; set; } = 20;
    public int QueueCapacity { get; set; } = 200;
    /// <summary>Publish uploaded documents immediately. When false they wait for review (POST .../publish).</summary>
    public bool PublishUploadsByDefault { get; set; } = true;
    /// <summary>Sync every knowledge base that has a source folder once at startup.</summary>
    public bool SyncOnStartup { get; set; } = true;
    /// <summary>Start website crawls of never-crawled knowledge bases at startup (off: start the first crawl manually).</summary>
    public bool CrawlOnStartup { get; set; }

    public static readonly string[] SupportedExtensions = [".md", ".markdown", ".txt", ".html", ".htm", ".pdf", ".docx"];
}
