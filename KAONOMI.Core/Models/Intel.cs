using System.Collections.Generic;
namespace KAONOMI.Core.Models;

public class Intel : EntityBase, ISoftDeletable, ITagged
{
    #region "if"
  
    public List<EntityWithWeight> IncludedEntities { get; set; } = new List<EntityWithWeight>();
    public List<EntityWithWeight> ExcludedEntities { get; set; } = new List<EntityWithWeight>();
    public int TriggerLevel { get; set; } = 0; // 0 to 100 pourcent
    public string? Instructions { get; set; }
    public bool isFuzzy { get; set; } = false;
    public bool isDeleted { get; set; } = false;
    #endregion
    #region "then"
    public List<string> Locations { get; set; } = new List<string>();
    public List<string> Times { get; set; } = new List<string>();
    public List<string> RelatedEntities { get; set; } = new List<string>();

    public List<string> Questions { get; set; } = new List<string>();
    #endregion

}

public class EntityWithWeight
{ 
    public string EntityName { get; set; }
    public string EntityWeight { get; set; }

}