using Microsoft.EntityFrameworkCore;
using OrderProcessingApp.Data;
using OrderProcessingApp.Models;

namespace OrderProcessingApp.Services;

public sealed class DistributionCentreResolutionResult
{
    public bool IsResolved { get; init; }
    public string OriginalInput { get; init; } = string.Empty;
    public string NormalizedInput { get; init; } = string.Empty;
    public DistributionCentre? DistributionCentre { get; init; }
}

public interface IDistributionCentreResolver
{
    Task<DistributionCentreResolutionResult> ResolveFromCsvAsync(string rawDistributionCentre, CancellationToken cancellationToken = default);
}

public sealed class DistributionCentreResolver : IDistributionCentreResolver
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<DistributionCentreResolver> _logger;

    public DistributionCentreResolver(AppDbContext dbContext, ILogger<DistributionCentreResolver> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<DistributionCentreResolutionResult> ResolveFromCsvAsync(string rawDistributionCentre, CancellationToken cancellationToken = default)
    {
        var normalizedInput = Normalize(rawDistributionCentre);
        if (string.IsNullOrWhiteSpace(normalizedInput))
        {
            _logger.LogWarning("CSV DC resolution failed: empty input after normalization. Raw='{Raw}'", rawDistributionCentre);
            return new DistributionCentreResolutionResult
            {
                IsResolved = false,
                OriginalInput = rawDistributionCentre,
                NormalizedInput = normalizedInput
            };
        }

        var candidates = await _dbContext.DistributionCentres
            .IgnoreQueryFilters()
            .ToListAsync(cancellationToken);

        var match = candidates
            .Where(dc => Normalize(dc.Name) == normalizedInput || Normalize(dc.Code) == normalizedInput)
            .OrderByDescending(dc => dc.IsActive)
            .FirstOrDefault();

        if (match is null)
        {
            _logger.LogWarning(
                "CSV DC resolution failed. Raw='{Raw}', Normalized='{Normalized}'. No default fallback applied.",
                rawDistributionCentre,
                normalizedInput);

            return new DistributionCentreResolutionResult
            {
                IsResolved = false,
                OriginalInput = rawDistributionCentre,
                NormalizedInput = normalizedInput
            };
        }

        _logger.LogInformation(
            "CSV DC resolution succeeded. Raw='{Raw}', Normalized='{Normalized}', DistributionCentreId={DistributionCentreId}, DistributionCentreName='{DistributionCentreName}', IsActive={IsActive}",
            rawDistributionCentre,
            normalizedInput,
            match.Id,
            match.Name,
            match.IsActive);

        return new DistributionCentreResolutionResult
        {
            IsResolved = true,
            OriginalInput = rawDistributionCentre,
            NormalizedInput = normalizedInput,
            DistributionCentre = match
        };
    }

    private static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        return string.Join(' ', input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }
}
