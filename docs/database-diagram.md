# Glosify Database Diagram

This diagram is based on `GlosifyContext` and the current EF Core model snapshot. ASP.NET Identity tables are simplified to their relationship-bearing columns.

```mermaid
erDiagram
    AspNetUsers {
        string Id PK
        string UserName
        string Email
        string NormalizedUserName
        string NormalizedEmail
    }

    AspNetRoles {
        string Id PK
        string Name
        string NormalizedName
    }

    AspNetUserClaims {
        int Id PK
        string UserId FK
        string ClaimType
        string ClaimValue
    }

    AspNetUserLogins {
        string LoginProvider PK
        string ProviderKey PK
        string UserId FK
    }

    AspNetUserRoles {
        string UserId PK
        string RoleId PK
    }

    AspNetUserTokens {
        string UserId PK
        string LoginProvider PK
        string Name PK
        string Value
    }

    AspNetRoleClaims {
        int Id PK
        string RoleId FK
        string ClaimType
        string ClaimValue
    }

    Quizzes {
        guid Id PK
        string UserId FK
        guid CollectionId FK
        string Name
        string SourceLanguage
        string TargetLanguage
        string Language
        datetimeoffset CreatedAt
        bool IsSongQuiz
        bool IsPublic
        guid OriginalQuizId
        string ProcessingStatus
        string ProcessingMessage
        bool AnkiTrackingEnabled
        bool AnkiTrackWordsForward
        bool AnkiTrackWordsReverse
        bool AnkiTrackSentencesForward
        bool AnkiTrackSentencesReverse
    }

    Collections {
        guid Id PK
        string UserId FK
        guid ParentCollectionId FK
        string Name
        string Language
        datetimeoffset CreatedAt
        bool IsPublic
        guid OriginalCollectionId
    }

    words {
        string id PK
        guid quiz_id
        string lemma
        string translation
    }

    quiz_sentences {
        guid id PK
        guid quiz_id FK
        string text
        string translation
        datetimeoffset created_at
    }

    assistant_threads {
        guid id PK
        string user_id FK
        guid quiz_id FK
        guid context_quiz_id FK
        string title
        datetimeoffset created_at
        datetimeoffset updated_at
    }

    assistant_messages {
        guid id PK
        guid thread_id FK
        guid context_quiz_id FK
        int sequence
        string role
        string status
        string content_json
        string pending_changes_json
        datetimeoffset created_at
    }

    AiCreditAccounts {
        string UserId PK
        int BalanceCredits
        int ReservedCredits
        datetimeoffset TrialGrantedAt
        datetimeoffset CreatedAt
        datetimeoffset UpdatedAt
        bytes RowVersion
    }

    AiCreditTransactions {
        guid Id PK
        string UserId FK
        guid ReservationId
        string Kind
        int CreditAmount
        int BalanceAfterCredits
        int ReservedAfterCredits
        string Provider
        string Model
        string Feature
        string Operation
        int PromptTokens
        int CandidateTokens
        int ThoughtTokens
        int ToolPromptTokens
        int TotalTokens
        string ActorUserId
        string Note
        string RelatedEntityType
        string RelatedEntityId
        datetimeoffset CreatedAt
    }

    BookDocuments {
        guid Id PK
        string UserId FK
        string Title
        string OriginalFileName
        string BlobName
        int PageCount
        string ProcessingStatus
        string ProcessingMessage
        datetimeoffset CreatedAt
        datetimeoffset UpdatedAt
    }

    BookPages {
        guid Id PK
        guid BookDocumentId FK
        int PageNumber
        string Text
        string ExtractionWarning
    }

    AspNetUsers ||--o{ AspNetUserClaims : has
    AspNetUsers ||--o{ AspNetUserLogins : has
    AspNetUsers ||--o{ AspNetUserRoles : has
    AspNetUsers ||--o{ AspNetUserTokens : has
    AspNetRoles ||--o{ AspNetUserRoles : grants
    AspNetRoles ||--o{ AspNetRoleClaims : has

    AspNetUsers ||--o{ Quizzes : owns
    AspNetUsers ||--o{ Collections : owns
    Collections ||--o{ Collections : contains
    Collections ||--o{ Quizzes : groups
    Quizzes ||--o{ quiz_sentences : has
    Quizzes ||--o{ words : logical_quiz_id

    AspNetUsers ||--o{ assistant_threads : starts
    Quizzes ||--o{ assistant_threads : scoped_to
    Quizzes ||--o{ assistant_threads : context_for
    assistant_threads ||--o{ assistant_messages : has
    Quizzes ||--o{ assistant_messages : context_for

    AspNetUsers ||--|| AiCreditAccounts : has
    AspNetUsers ||--o{ AiCreditTransactions : records
    AspNetUsers ||--o{ BookDocuments : uploads
    BookDocuments ||--o{ BookPages : contains
```

## Notes

- `words.quiz_id` is used throughout the application as a quiz association, but the current EF Core model snapshot does not configure it as an enforced foreign key.
- `Collections.ParentCollectionId` is a self-reference with restricted delete behavior.
- `Quizzes.CollectionId`, `assistant_threads.quiz_id`, `assistant_threads.context_quiz_id`, and `assistant_messages.context_quiz_id` are nullable in the model.
- `BookPages` has a unique index on `(BookDocumentId, PageNumber)`.
- `assistant_messages` has a unique index on `(thread_id, sequence)`.
