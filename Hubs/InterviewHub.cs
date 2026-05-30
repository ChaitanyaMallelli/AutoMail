using Microsoft.AspNetCore.SignalR;
using JobAutomation.Services;
using JobAutomation.Data;

namespace JobAutomation.Hubs;

public class InterviewHub : Hub
{
    private readonly GeminiService _geminiService;
    private readonly AppDbContext _dbContext;

    public InterviewHub(GeminiService geminiService, AppDbContext dbContext)
    {
        _geminiService = geminiService;
        _dbContext = dbContext;
    }

    public async Task SendAudioChunk(string base64Audio, int jobId)
    {
        try
        {
            var audioBytes = Convert.FromBase64String(base64Audio);

            // Fetch job context
            var job = await _dbContext.JobPosts.FindAsync(jobId);
            if (job == null) return;

            // In a real production app, we would stream this directly to Gemini Live API.
            // For V1, we simulate a batched audio prompt.
            var responseText = await _geminiService.ProcessLiveInterviewAudioAsync(audioBytes, job);

            // Stream the hint back to the client
            if (!string.IsNullOrEmpty(responseText))
            {
                await Clients.Caller.SendAsync("ReceiveHint", responseText);
            }
        }
        catch (Exception ex)
        {
            await Clients.Caller.SendAsync("ReceiveError", ex.Message);
        }
    }
}
