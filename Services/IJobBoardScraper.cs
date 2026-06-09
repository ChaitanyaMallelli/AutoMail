using JobAutomation.Models;

namespace JobAutomation.Services;

public interface IJobBoardScraper
{
    string BoardName { get; }
    Task<List<ScoutedJob>> ScrapePostsAsync(List<string> keywords, CancellationToken cancellationToken = default);
}
