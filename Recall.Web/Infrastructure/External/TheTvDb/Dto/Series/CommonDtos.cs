using System.Text.Json.Serialization;

namespace Recall.Web.Infrastructure.External.TheTvDb.Dto.Series;

public sealed class AliasDto
{
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class StatusDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("keepUpdated")]
    public bool? KeepUpdated { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("recordType")]
    public string? RecordType { get; init; }
}

public sealed class RelationDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("typeName")]
    public string? TypeName { get; init; }
}

public sealed class ParentCompanyDto
{
    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("relation")]
    public RelationDto? Relation { get; init; }
}

public sealed class TagOptionDto
{
    [JsonPropertyName("helpText")]
    public string? HelpText { get; init; }

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("tag")]
    public int? Tag { get; init; }

    [JsonPropertyName("tagName")]
    public string? TagName { get; init; }
}

public sealed class CompanyDto
{
    [JsonPropertyName("activeDate")]
    public string? ActiveDate { get; init; }

    [JsonPropertyName("aliases")]
    public List<AliasDto>? Aliases { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("inactiveDate")]
    public string? InactiveDate { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("nameTranslations")]
    public List<string>? NameTranslations { get; init; }

    [JsonPropertyName("overviewTranslations")]
    public List<string>? OverviewTranslations { get; init; }

    [JsonPropertyName("primaryCompanyType")]
    public int? PrimaryCompanyType { get; init; }

    [JsonPropertyName("slug")]
    public string? Slug { get; init; }

    [JsonPropertyName("parentCompany")]
    public ParentCompanyDto? ParentCompany { get; init; }

    [JsonPropertyName("tagOptions")]
    public List<TagOptionDto>? TagOptions { get; init; }
}

public sealed class CompaniesDto
{
    [JsonPropertyName("studio")]
    public List<CompanyDto>? Studio { get; init; }

    [JsonPropertyName("network")]
    public List<CompanyDto>? Network { get; init; }

    [JsonPropertyName("production")]
    public List<CompanyDto>? Production { get; init; }

    [JsonPropertyName("distributor")]
    public List<CompanyDto>? Distributor { get; init; }

    [JsonPropertyName("special_effects")]
    public List<CompanyDto>? SpecialEffects { get; init; }
}

public sealed class SeasonTypeDto
{
    [JsonPropertyName("alternateName")]
    public string? AlternateName { get; init; }

    [JsonPropertyName("id")]
    public int? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
}