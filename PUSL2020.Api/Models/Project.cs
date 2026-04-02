namespace PUSL2020.Api.Models;

public class Project
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Abstract { get; set; } = string.Empty;
    public string TechStack { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";

    public int ResearchAreaId { get; set; }
    public ResearchArea? ResearchArea { get; set; }

    public int StudentId { get; set; }
    public User? Student { get; set; }

    public int? SupervisorId { get; set; }
    public User? Supervisor { get; set; }
}
