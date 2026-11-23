using PopTranslate.Core.Models;

namespace PopTranslate.Core.Contracts;

public interface IScreenshotCaptureService
{
    bool IsCaptureActive { get; }

    Task<CapturedScreenshot?> CaptureRegionAsync(CancellationToken cancellationToken = default);

    void CancelActiveCapture();
}
