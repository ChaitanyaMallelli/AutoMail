using JobAutomation.Data;
using JobAutomation.Models;
using JobAutomation.Services;
using Microsoft.EntityFrameworkCore;

namespace JobAutomation.Workers;

public class JobScoutBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<JobScoutBackgroundService> _logger;
    private readonly TimeSpan _checkInterval = TimeSpan.FromHours(6); // Run every 6 hours

    // The user's exact keywords
    private readonly List<string> _searchKeywords = new()
    {
        "hiring for dotnet developer",
        "dotnet developer",
        ".net developer",
        ".net developer banglore",
        ".net developer in dubai"
    };

    public JobScoutBackgroundService(IServiceProvider serviceProvider, ILogger<JobScoutBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job Scout Background Service is starting.");

        // Wait a few minutes on startup so we don't instantly launch Playwright during dev
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunScoutCycleAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred during job scouting cycle.");
            }

            _logger.LogInformation("Job Scout sleeping for {Interval}", _checkInterval);
            await Task.Delay(_checkInterval, stoppingToken);
        }
    }

    private async Task RunScoutCycleAsync(CancellationToken stoppingToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var scoutManager = scope.ServiceProvider.GetRequiredService<JobScoutManager>();
        
        await scoutManager.RunScoutCycleAsync(stoppingToken);
    }
}
