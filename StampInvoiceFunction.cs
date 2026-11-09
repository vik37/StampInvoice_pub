using Azure.Storage.Blobs;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StampInvoice;

public class StampInvoiceFunction
{
    private readonly ILogger<StampInvoiceFunction> _logger;
	private readonly BlobServiceClient _blobServiceClient;
	private readonly string _containerName;

	public StampInvoiceFunction(ILogger<StampInvoiceFunction> logger, BlobServiceClient blobService, IConfiguration configuration)
    {
        _logger = logger;
		_blobServiceClient = blobService;
		_containerName = configuration["BlobContainerName"] ?? string.Empty;
    }

	[Function("stamp-invoice")]
	public async Task<HttpResponseData> Run([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req)
    {
		if (string.IsNullOrEmpty(_containerName))
			_logger.LogError("BlobContainerName configuration is missing.");

		var data = await JsonSerializer.DeserializeAsync<InvoiceStampRequest>(req.Body);

		if (data is null)
			_logger.LogWarning("Invalid Request: Type of {Entity} can not be null", typeof(InvoiceStampRequest).Name);

		var prefix = $"/{_containerName}/";

		var uri = new Uri(data.BlobPath);
		var relativePath = uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
							? uri.AbsolutePath.Substring(prefix.Length)
							: uri.AbsolutePath.TrimStart('/');

		_logger.LogInformation("Parsed Relative Path for Blob: {RelativePath}", relativePath);

		var containerClient = _blobServiceClient.GetBlobContainerClient(_containerName);
		var blobClient = containerClient.GetBlobClient(relativePath);

		using var pdfMemoryStream = new MemoryStream();
		await blobClient.DownloadToAsync(pdfMemoryStream);
		pdfMemoryStream.Position = 0;

		using var outputStream = new MemoryStream();
		{
			PdfDocument document = PdfReader.Open(pdfMemoryStream, PdfDocumentOpenMode.Modify);
			PdfPage page = document.Pages[document.Pages.Count - 1];
			XGraphics gfx = XGraphics.FromPdfPage(page);

			XFont labelFont = new XFont("Times New Roman", 10, XFontStyle.Italic);
			XFont valueFont = new XFont("Times New Roman", 10, XFontStyle.BoldItalic);
			XBrush brush = XBrushes.MediumBlue;

			double startY = page.Height.Point - 135;
			double lineSpacing = 16.5;

			// 1. Authorized signature
			gfx.DrawString("Authorized signature:", labelFont, XBrushes.DarkSlateGray, new XPoint(50, startY));
			gfx.DrawString("John Doe", valueFont, brush, new XPoint(215, startY));

			// 2. Name and surname
			gfx.DrawString("Name and surname:", labelFont, XBrushes.DarkSlateGray, new XPoint(50, startY + lineSpacing));
			gfx.DrawString("Jane Smith", valueFont, brush, new XPoint(215, startY + lineSpacing));

			// 3. Bank representative's signature
			gfx.DrawString("Bank representative's signature:", labelFont, XBrushes.DarkSlateGray, new XPoint(50, startY + (lineSpacing * 2)));
			gfx.DrawString("J. Smith (Verified)", valueFont, brush, new XPoint(215, startY + (lineSpacing * 2)));

			// 4. Official bank stamp
			gfx.DrawString("Official bank stamp:", labelFont, XBrushes.DarkSlateGray, new XPoint(50, startY + (lineSpacing * 3)));
			gfx.DrawString("NLB Banka AD Skopje - Verified Stamp", valueFont, brush, new XPoint(215, startY + (lineSpacing * 3)));

			// 5. Date of payment
			gfx.DrawString("Date of payment:", labelFont, XBrushes.DarkSlateGray, new XPoint(50, startY + (lineSpacing * 4)));
			gfx.DrawString(DateTime.UtcNow.ToString("dd.MM.yyyy"), valueFont, brush, new XPoint(215, startY + (lineSpacing * 4)));

			var stampPath = Path.Combine(AppContext.BaseDirectory, "img", "stamp.png");
			XImage stampImage = XImage.FromFile(stampPath);
			gfx.DrawImage(stampImage, 420, startY - 10, 85, 85);

			document.Save(outputStream, false);
		}

		outputStream.Position = 0;

		await blobClient.UploadAsync(outputStream, overwrite: true);

		return req.CreateResponse(HttpStatusCode.OK);

	}
}

public class InvoiceStampRequest
{
	[JsonPropertyName("orderNumber")]
	public string OrderNumber { get; set; } = string.Empty;

	[JsonPropertyName("blobPath")]
	public string BlobPath { get; set; } = string.Empty;
}