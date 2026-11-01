using System;
using System.Collections.Generic;

namespace KAONOMI.Core.Models;

/// <summary>An element of a cluster: the window it belongs to, its object, its title and the data it was linked with.</summary>
public class ClusterMember
{
    /// <summary>The window of the element (Ideas, Entity, YouTube, Image analysis…).</summary>
    public string windowName { get; set; } = string.Empty;
    public string objectId { get; set; } = string.Empty;
    public string title { get; set; } = string.Empty;
    /// <summary>The data of the window of the element when it was clustered (JSON), optional.</summary>
    public string category { get; set; } = string.Empty; // graphnode, infra, bpmnai, 
    public string? context { get; set; }
    /// <summary>When the element starts to belong to the cluster (optional).</summary>
    public DateTime? dateFrom { get; set; }
    /// <summary>When the element stops belonging to the cluster (optional).</summary>
    public DateTime? dateTo { get; set; }
    /// <summary>How probable the belonging of the element to the cluster is, 0 to 100 (100: sure).</summary>
    public int probability { get; set; } = 100;
}

/// <summary>A point of the drawing of a cluster, in the coordinates of the graph.</summary>
public class ClusterPoint
{
    public double x { get; set; }
    public double y { get; set; }
}

/// <summary>
/// A cluster: a group of elements of the graph of relations drawn around with the pen of <c>/relation</c>, with a name,
/// a description and tags. Never erased (<see cref="ISoftDeletable"/>); the tags follow <see cref="TagRules"/>.
/// </summary>
public class Cluster : EntityBase, ISoftDeletable, ITagged
{
    /// <summary>Never erased: marked deleted instead (<see cref="ISoftDeletable"/>).</summary>
    public bool isDeleted { get; set; } = false;

    public string name { get; set; } = string.Empty;
    public string description { get; set; } = string.Empty;
    /// <summary>Where the cluster was drawn: "relation" (the graph of relations).</summary>
    public string category { get; set; } = "relation";
    /// <summary>The elements inside the drawing.</summary>
    public List<ClusterMember> members { get; set; } = new List<ClusterMember>();
    /// <summary>The drawing of the pen, simplified, in the coordinates of the graph (shown smoothed by "Show clusters").</summary>
    public List<ClusterPoint> path { get; set; } = new List<ClusterPoint>();
    /// <summary>The tags: #TOPIC, !ALERT, ?QUESTION, &amp;LINK, in upper case, no space (<see cref="TagRules"/>).</summary>
    public List<string> Tags { get; set; } = new List<string>();
    /// <summary>When the cluster starts to hold (optional).</summary>
    public DateTime? dateFrom { get; set; }
    /// <summary>When the cluster stops holding (optional).</summary>
    public DateTime? dateTo { get; set; }
    /// <summary>How probable the cluster is, 0 to 100 (100: sure). Clamped by the server.</summary>
    public int probability { get; set; } = 100;

    /// <summary>Keeps the probabilities between 0 and 100 and the dates in order (dateFrom before dateTo).</summary>
    public void Normalize()
    {
        probability = Math.Clamp(probability, 0, 100);
        if (dateFrom is { } f && dateTo is { } t && t < f)
            (dateFrom, dateTo) = (t, f);
        foreach (var m in members)
        {
            m.probability = Math.Clamp(m.probability, 0, 100);
            if (m.dateFrom is { } mf && m.dateTo is { } mt && mt < mf)
                (m.dateFrom, m.dateTo) = (mt, mf);
        }
    }
}
