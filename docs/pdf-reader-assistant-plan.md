# Page-Aware PDF Reader Assistant Implementation Guide

## Summary

Add a Books area where users can upload text-based PDFs, read the actual PDF page in Glosify, select a target quiz, and ask the assistant to generate quiz content from the current page. The original PDF is stored in Azure Blob Storage, extracted page text is stored in SQL, and assistant-generated words or sentences are queued for review before the user applies them.

This is a v1 plan. It supports selectable-text PDFs only, not OCR for scanned books.

## Product Shape

- Books live in a user-level book library, separate from quizzes.
- The reader displays the real PDF page using PDF.js.
- The reader includes a target quiz selector.
- The assistant is enabled only when a target quiz is selected.
- The assistant receives the current page as context server-side.
- Generated quiz content uses the existing assistant pending-change workflow.

## 1. Add Packages And Configuration

Install the required packages:

```powershell
dotnet add Glosify\Glosify.csproj package Azure.Storage.Blobs
dotnet add Glosify\Glosify.csproj package UglyToad.PdfPig
```

Add Blob Storage configuration:

```json
"BlobStorage": {
  "ConnectionString": "",
  "ContainerName": "books"
}
```

Production environment variables:

```powershell
BlobStorage__ConnectionString="<azure-storage-connection-string>"
BlobStorage__ContainerName="books"
```

For local development, use Azurite or a development Azure Storage account.

## 2. Add Database Entities

Add `BookDocument`:

```csharp
public class BookDocument
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = "";
    public string Title { get; set; } = "";
    public string OriginalFileName { get; set; } = "";
    public string BlobName { get; set; } = "";
    public int PageCount { get; set; }
    public string ProcessingStatus { get; set; } = "Ready";
    public string? ProcessingMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

Add `BookPage`:

```csharp
public class BookPage
{
    public Guid Id { get; set; }
    public Guid BookDocumentId { get; set; }
    public int PageNumber { get; set; }
    public string Text { get; set; } = "";
    public string? ExtractionWarning { get; set; }
    public BookDocument BookDocument { get; set; } = null!;
}
```

Update `GlosifyContext`:

```csharp
public DbSet<BookDocument> BookDocuments { get; set; }
public DbSet<BookPage> BookPages { get; set; }
```

Configure the model:

- `BookDocument.UserId` is required, max length 450, and indexed.
- `BookDocument.Title` is required, max length 256.
- `BookDocument.OriginalFileName` is required, max length 512.
- `BookDocument.BlobName` is required, max length 1024.
- `BookDocument.ProcessingStatus` is required, max length 64.
- `BookDocument.ProcessingMessage` max length 512.
- `BookPage.Text` is required.
- Add a unique index on `{ BookDocumentId, PageNumber }`.
- Delete `BookPage` rows when the owning `BookDocument` is deleted.
- Delete `BookDocument` rows when the owning user is deleted.

Create and apply the migration:

```powershell
dotnet ef migrations add AddBookDocuments --project Glosify\Glosify.csproj
dotnet ef database update --project Glosify\Glosify.csproj
```

## 3. Add Blob Storage Service

Create `BlobStorageOptions`:

```csharp
public sealed class BlobStorageOptions
{
    public string ConnectionString { get; set; } = "";
    public string ContainerName { get; set; } = "books";
}
```

Create `IBookFileStorage`:

```csharp
public interface IBookFileStorage
{
    Task<string> UploadAsync(Guid userId, Guid documentId, IFormFile file, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken);
    Task DeleteAsync(string blobName, CancellationToken cancellationToken);
}
```

Implement `AzureBlobBookFileStorage` with `Azure.Storage.Blobs`.

Use blob names in this format:

```text
users/{userId}/books/{documentId}.pdf
```

Register services in `Program.cs`:

```csharp
builder.Services.Configure<BlobStorageOptions>(builder.Configuration.GetSection("BlobStorage"));
builder.Services.AddSingleton<IBookFileStorage, AzureBlobBookFileStorage>();
```

The storage service should create the container if it does not exist.

## 4. Add PDF Text Extraction Service

Create this record:

```csharp
public sealed record ExtractedPdfPage(int PageNumber, string Text, string? Warning);
```

Create `IPdfTextExtractionService`:

```csharp
public interface IPdfTextExtractionService
{
    Task<IReadOnlyList<ExtractedPdfPage>> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken);
}
```

Implement it with PdfPig:

- Open the PDF from the stream.
- For each page, read `page.Text`.
- Trim extracted text.
- If a page has no extracted text, save warning: `No selectable text found on this page.`
- Return one `ExtractedPdfPage` per PDF page.
- Do not add OCR in v1.

Register the service:

```csharp
builder.Services.AddScoped<IPdfTextExtractionService, PdfPigTextExtractionService>();
```

## 5. Add Book Document Service

Create `IBookDocumentService`:

```csharp
public interface IBookDocumentService
{
    Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken);
    Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken);
    Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken);
    Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken);
    Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken);
}
```

Upload behavior:

1. Reject missing or empty files.
2. Require `.pdf` extension or `application/pdf` content type.
3. Enforce a 25 MB size limit.
4. Create a `BookDocument` id.
5. Upload the original PDF to Blob Storage.
6. Re-open the uploaded file stream or copy it to memory once before upload/extraction.
7. Extract page text.
8. Save `BookDocument` and `BookPage` rows.
9. Set `PageCount` from extracted page count.
10. Set `ProcessingStatus` to `Ready`.

If extraction fails after upload, delete the blob and return a validation-style error. For v1, synchronous upload and extraction is acceptable.

Register:

```csharp
builder.Services.AddScoped<IBookDocumentService, BookDocumentService>();
```

## 6. Add Books Controller

Create `BooksController` with authorization enabled.

Routes:

- `GET /Books`: list the current user's uploaded books.
- `POST /Books/Upload`: upload and process a PDF.
- `GET /Books/Read/{id}?quizId={quizId}`: open the reader.
- `GET /Books/File/{id}`: stream the PDF after ownership check.
- Optional `GET /Books/Page/{id}/{pageNumber}`: return extracted page text for debugging.

Important controller rules:

- Always use `User.GetUserId()`.
- Never expose direct Azure Blob URLs.
- Always verify document ownership before listing pages or streaming files.
- Use `File(stream, "application/pdf", enableRangeProcessing: true)` for the PDF stream so PDF.js can request ranges.

## 7. Add Reader View Models

Create a Books index view model:

```csharp
public sealed class BookLibraryViewModel
{
    public IReadOnlyList<BookDocument> Books { get; set; } = [];
}
```

Create a reader view model:

```csharp
public sealed class BookReaderViewModel
{
    public BookDocument Book { get; set; } = null!;
    public IReadOnlyList<Quiz> Quizzes { get; set; } = [];
    public Guid? SelectedQuizId { get; set; }
}
```

## 8. Add Reader UI

Add:

```text
Views/Books/Index.cshtml
Views/Books/Read.cshtml
```

Library page:

- Upload form.
- List uploaded books.
- Read button for each book.

Reader page:

- Book title.
- Quiz selector.
- PDF page canvas.
- Previous and next buttons.
- Current page indicator.
- Existing assistant panel when `SelectedQuizId` is present.

Use PDF.js from CDN for v1.

Reader JavaScript responsibilities:

- Load `/Books/File/{documentId}` into PDF.js.
- Render one page at a time.
- Track `currentPage`.
- Update the page indicator.
- Update the assistant panel dataset:

```html
data-document-id="{documentId}"
data-current-page="1"
```

When the user changes page, update `data-current-page`.

## 9. Extend Assistant Input

Update `AssistantController.SendMessageInput`:

```csharp
public sealed class SendMessageInput
{
    public string Message { get; set; } = string.Empty;
    public string? FocusedWordId { get; set; }
    public string? Model { get; set; }
    public DocumentContextInput? DocumentContext { get; set; }
}

public sealed class DocumentContextInput
{
    public Guid DocumentId { get; set; }
    public int PageNumber { get; set; }
}
```

Update `assistant.js`:

- Read `panel.dataset.documentId`.
- Read `panel.dataset.currentPage`.
- Include `documentContext` when both are present.

Example request body:

```js
body: JSON.stringify({
    message,
    focusedWordId,
    model: modelSelect?.value || null,
    documentContext: panel.dataset.documentId ? {
        documentId: panel.dataset.documentId,
        pageNumber: Number(panel.dataset.currentPage || 1)
    } : null
})
```

## 10. Inject Page Context Server-Side

Extend `IAssistantOrchestrator.SendMessageAsync` to accept optional document context.

Create a small internal context model:

```csharp
public sealed record AssistantDocumentContext(Guid DocumentId, int PageNumber);
```

In `AssistantOrchestrator`:

- Inject `IBookDocumentService`.
- If document context exists, load the owned page by `documentId`, `pageNumber`, and `userId`.
- If not found, throw or return a clear error.
- If page text is empty, add a clear assistant-facing context note.
- Add page context to `BuildSystemInstruction`.

Recommended context block:

```text
Current book page context:
- Document: "{title}"
- Page: {pageNumber}
- The user is reading this page now.
- When the user says "this page", "here", "from the book", or "from what I am reading", use this page text.
- Use only this page text unless the user asks for something else.

Page text:
---
{text}
---
```

Do not accept page text from the browser. Only accept document id and page number.

## 11. Update Assistant Rules

Update `BuildSystemInstruction` to include these rules when page context exists:

- If the user asks to make a quiz from the current page, extract useful vocabulary from the page text.
- Use `add_word` for vocabulary and `add_sentence` for natural full sentences.
- Skip words that are too basic unless central to the page.
- Keep all changes queued for review.
- If the page has no selectable text, explain that the page could not be read and ask the user to choose another page or paste text.

No new assistant tool is needed in v1 because the only document context is the current page.

## 12. Wire Reader To Quiz

Because books live outside quizzes:

- Reader URL accepts an optional `quizId`.
- If no quiz is selected, show the reader and quiz selector, but keep assistant disabled.
- When the user selects a quiz, navigate to:

```text
/Books/Read/{documentId}?quizId={quizId}
```

- The assistant panel uses the existing route:

```text
/Quiz/{quizId}/Assistant/Send
```

That keeps generated content attached to the selected quiz.

## 13. Tests

Add tests for these scenarios:

- Text PDF extraction returns page text.
- Empty page returns extraction warning.
- Upload rejects non-PDF files.
- Upload rejects empty files.
- Upload creates one `BookDocument` and the expected `BookPage` rows.
- User cannot stream another user's PDF.
- User cannot use another user's document as assistant context.
- Assistant loads current page text server-side.
- Empty page text produces a useful error or assistant response.
- Generated words and sentences remain pending until Apply.

## Manual Acceptance Scenarios

Verify these manually:

1. Upload a text-based PDF.
2. Open the reader and see the real PDF page.
3. Move to another page and confirm the page indicator changes.
4. Select a target quiz.
5. Ask: `Make a quiz from this page.`
6. Confirm proposed words or sentences appear in the assistant review card.
7. Click Apply and confirm the quiz updates.
8. Open a page with no selectable text and confirm the assistant explains that it cannot read the page in v1.

## Assumptions And Defaults

- v1 supports text/selectable PDFs only.
- OCR is out of scope.
- Full-book search is out of scope.
- Chapter detection is out of scope.
- Azure Blob Storage stores original PDFs.
- SQL stores page-level extracted text.
- The assistant receives only the current page as context.
- The browser sends document id and page number, not raw text.
- Generated quiz content is queued for review, not saved immediately.
