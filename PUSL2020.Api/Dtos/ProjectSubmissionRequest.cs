namespace PUSL2020.Api.Dtos;

public class ProjectSubmissionRequest
{
    public string Title { get; set; } = string.Empty;
    public string Abstract { get; set; } = string.Empty;
    public string TechStack { get; set; } = string.Empty;
    public int ResearchAreaId { get; set; }
}
