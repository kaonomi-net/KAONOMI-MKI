using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace KAONOMI.Chat.Models;

/// <summary>
/// Ontology node exchanged with the <c>/ontology</c> routes (<c>Ontology</c> on the server side).
/// Nodes form a tree through <see cref="NodeParentId"/>.
/// </summary>
public sealed class OntologyEntry
{
    [JsonPropertyName("appName")]
    /// <summary>Application that created or owns this node.</summary>
    public string? AppName { get; set; }

    [JsonPropertyName("date")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    /// <summary>
    /// Document timestamp when known. <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/>: the server
    /// default <c>0001-01-01T00:00:00</c> has no offset and cannot be read as a local <see cref="DateTimeOffset"/>.
    /// </summary>
    public DateTime? Date { get; set; }

    [JsonPropertyName("machineName")]
    /// <summary>Machine name recorded with the node.</summary>
    public string? MachineName { get; set; }

    [JsonPropertyName("userName")]
    /// <summary>User name recorded with the node.</summary>
    public string? UserName { get; set; }

    [JsonPropertyName("comment")]
    /// <summary>Free-text note.</summary>
    public string? Comment { get; set; }

    /// <summary>
    /// Document id. Always sent: the client generates a GUID for new nodes, because the server
    /// model defaults to an empty id and children must be able to reference their parent.
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("dateCreated")]
    /// <summary>Creation time as stored by the server (<c>yyyy-MM-dd HH:mm:ss</c>).</summary>
    public string? DateCreated { get; set; }

    [JsonPropertyName("name")]
    /// <summary>Node label shown in the tree.</summary>
    public string? Name { get; set; }

    [JsonPropertyName("entityType")]
    /// <summary>Kind of entity this node represents.</summary>
    public string? EntityType { get; set; }

    [JsonPropertyName("description")]
    /// <summary>Description; this is the field searched by <c>/ontology/search</c>.</summary>
    public string? Description { get; set; }

    [JsonPropertyName("nodevirtualeftchildrenId")]
    /// <summary>
    /// Ids of the virtual parents: besides its real parent, the node (with all its children) is also shown under each of them.
    /// </summary>
    public List<string>? VirtualParentIds { get; set; }

    [JsonPropertyName("nodeparentId")]
    /// <summary>Id of the parent node, or empty for a root node.</summary>
    public string? NodeParentId { get; set; }

    [JsonPropertyName("sentencesExemples")]
    /// <summary>Example sentences illustrating the node.</summary>
    public List<string>? SentencesExemples { get; set; }

    [JsonPropertyName("angryLevel")]
    public int? AngryLevel { get; set; }

    [JsonPropertyName("sadLevel")]
    public int? SadLevel { get; set; }

    [JsonPropertyName("happyLevel")]
    public int? HappyLevel { get; set; }

    [JsonPropertyName("fearLevel")]
    public int? FearLevel { get; set; }

    [JsonPropertyName("surpriseLevel")]
    public int? SurpriseLevel { get; set; }

    [JsonPropertyName("disgustLevel")]
    public int? DisgustLevel { get; set; }

    [JsonPropertyName("isOntologyClass")]
    /// <summary>True for a class node, false for an instance.</summary>
    public bool IsOntologyClass { get; set; }

    [JsonPropertyName("symbol")]
    /// <summary>Symbol associated with the node.</summary>
    public string? Symbol { get; set; }

    [JsonPropertyName("synonyms")]
    public List<string>? Synonyms { get; set; }

    /// <summary>The title of the node (besides its name).</summary>
    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>The tags: #TOPIC, upper case, '-' for the spaces (<see cref="Services.MetaTags"/>).</summary>
    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; }

    /// <summary>The events: @EVENT.</summary>
    [JsonPropertyName("events")]
    public List<string> Events { get; set; } = [];

    /// <summary>The signals: !SIGNAL.</summary>
    [JsonPropertyName("signals")]
    public List<string> Signals { get; set; } = [];

    [JsonPropertyName("relatedLinks")]
    public List<string>? RelatedLinks { get; set; }

    [JsonPropertyName("confidentiality")]
    /// <summary>
    /// Confidentiality level, upper case: PUBLIC(PU), RESTRICTED(NR), SECRET(NS), COSMIC TOP SECRET(CTS)
    /// (the server stores any other value as "UNCLASSIFIED").
    /// </summary>
    public string? Confidentiality { get; set; }

    /// <summary>Confidentiality levels from the lowest to the highest (same order and values as the server).</summary>
    public static readonly string[] ConfidentialityScale = ["PUBLIC(PU)", "RESTRICTED(NR)", "SECRET(NS)", "COSMIC TOP SECRET(CTS)"];

    [JsonPropertyName("confidentialityLevel")]
    /// <summary>
    /// Rank of <see cref="Confidentiality"/>: 1 PUBLIC(PU), 2 RESTRICTED(NR), 3 SECRET(NS), 4 COSMIC TOP SECRET(CTS),
    /// 0 otherwise. On the server both properties write the same field (and this one is read last), so it is always
    /// computed from <see cref="Confidentiality"/>; a received value is only used when no level text was received.
    /// </summary>
    public int ConfidentialityLevel
    {
        get => Array.FindIndex(ConfidentialityScale, l => string.Equals(l, Confidentiality?.Trim(), StringComparison.OrdinalIgnoreCase)) + 1;
        set
        {
            if (string.IsNullOrWhiteSpace(Confidentiality) && value is >= 1 and <= 4)
                Confidentiality = ConfidentialityScale[value - 1];
        }
    }

    [JsonPropertyName("relatedEntities")]
    /// <summary>Names of entities linked to this node.</summary>
    public List<string>? RelatedEntities { get; set; }

    [JsonPropertyName("isDeleted")]
    /// <summary>Soft-delete flag; deleted nodes are hidden unless "Show deleted" is on.</summary>
    public bool IsDeleted { get; set; }

    [JsonPropertyName("startDate")]
    /// <summary>Optional date from which the entity holds (exists, is valid); null: no start.</summary>
    public DateTime? StartDate { get; set; }

    [JsonPropertyName("endDate")]
    /// <summary>Optional date until which the entity holds; null: no end.</summary>
    public DateTime? EndDate { get; set; }

    /// <summary>Shallow copy with copied lists, so edits never alias the original.</summary>
    public OntologyEntry Copy()
    {
        var copy = (OntologyEntry)MemberwiseClone();
        copy.SentencesExemples = SentencesExemples is null ? null : [.. SentencesExemples];
        copy.Synonyms = Synonyms is null ? null : [.. Synonyms];
        copy.Tags = Tags is null ? null : [.. Tags];
        copy.Events = [.. Events];
        copy.Signals = [.. Signals];
        copy.RelatedLinks = RelatedLinks is null ? null : [.. RelatedLinks];
        copy.RelatedEntities = RelatedEntities is null ? null : [.. RelatedEntities];
        copy.VirtualParentIds = VirtualParentIds is null ? null : [.. VirtualParentIds];
        return copy;
    }
}
