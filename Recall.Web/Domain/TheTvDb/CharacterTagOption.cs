namespace Recall.Web.Domain.TheTvDb;

public sealed class CharacterTagOption
{
    public int Id { get; init; }
    public string? HelpText { get; init; }
    public string? Name { get; init; }
    public int? Tag { get; init; }
    public string? TagName { get; init; }
}