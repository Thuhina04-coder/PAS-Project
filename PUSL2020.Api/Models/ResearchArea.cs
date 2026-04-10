namespace PUSL2020.Api.Models;

public class ResearchArea
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public List<Project> Projects { get; set; } = new();
    public List<SupervisorResearchArea> SupervisorResearchAreas { get; set; } = new();
}
