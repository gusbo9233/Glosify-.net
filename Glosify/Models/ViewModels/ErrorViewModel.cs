namespace Glosify.Models.ViewModels
{
    public class ErrorViewModel
    {
        public string? RequestId { get; set; }

        public string Title { get; set; } = "Error";

        public string Message { get; set; } = "An error occurred while processing your request.";

        public string? ReturnPath { get; set; }

        public bool IsServiceWarmup { get; set; }

        public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);
    }
}
