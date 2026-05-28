using Microsoft.AspNetCore.Mvc;
using JobAutomation.Models;
using JobAutomation.Services;

namespace JobAutomation.Controllers;

public class UploadController : Controller
{
    private readonly JobProcessingService _jobProcessingService;
    private readonly ILogger<UploadController> _logger;

    public UploadController(JobProcessingService jobProcessingService, ILogger<UploadController> logger)
    {
        _jobProcessingService = jobProcessingService;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> ProcessText(string jobText)
    {
        if (string.IsNullOrWhiteSpace(jobText))
        {
            TempData["Error"] = "Please enter job post text.";
            return RedirectToAction("Index");
        }

        try
        {
            var jobId = await _jobProcessingService.ProcessTextAsync(jobText, JobSource.Upload);
            TempData["Success"] = "Job post processed successfully!";
            return RedirectToAction("Preview", "Email", new { id = jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing text upload");
            TempData["Error"] = $"Processing failed: {ex.Message}";
            return RedirectToAction("Index");
        }
    }

    [HttpPost]
    public async Task<IActionResult> ProcessFile(IFormFile file)
    {
        if (file == null || file.Length == 0)
        {
            TempData["Error"] = "Please select a file to upload.";
            return RedirectToAction("Index");
        }

        try
        {
            using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            var fileBytes = memoryStream.ToArray();

            int jobId;

            if (file.ContentType == "application/pdf")
            {
                jobId = await _jobProcessingService.ProcessPdfAsync(fileBytes, JobSource.Upload);
            }
            else if (file.ContentType.StartsWith("image/"))
            {
                jobId = await _jobProcessingService.ProcessImageAsync(fileBytes, file.ContentType, JobSource.Upload);
            }
            else
            {
                TempData["Error"] = "Unsupported file type. Please upload an image or PDF.";
                return RedirectToAction("Index");
            }

            TempData["Success"] = "File processed successfully!";
            return RedirectToAction("Preview", "Email", new { id = jobId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file upload");
            TempData["Error"] = $"Processing failed: {ex.Message}";
            return RedirectToAction("Index");
        }
    }
}
