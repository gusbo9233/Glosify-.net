using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiMonthlyBudgets",
                columns: table => new
                {
                    PeriodKey = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: false),
                    LimitMicros = table.Column<long>(type: "bigint", nullable: false),
                    SpentMicros = table.Column<long>(type: "bigint", nullable: false),
                    ReservedMicros = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiMonthlyBudgets", x => x.PeriodKey);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    SelectedQuizLanguageCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: true),
                    UserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    PasswordHash = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SecurityStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumber = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "bit", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "bit", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                    table.CheckConstraint("CK_AspNetUsers_SelectedQuizLanguageCode", "[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ('et', 'de', 'pl', 'uk')");
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AcsUserIdentities",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    AcsUserId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AcsUserIdentities", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_AcsUserIdentities_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiCreditAccounts",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    BalanceCredits = table.Column<int>(type: "int", nullable: false),
                    ReservedCredits = table.Column<int>(type: "int", nullable: false),
                    TrialGrantedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCreditAccounts", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_AiCreditAccounts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AiCreditTransactions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Kind = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreditAmount = table.Column<int>(type: "int", nullable: false),
                    BalanceAfterCredits = table.Column<int>(type: "int", nullable: false),
                    ReservedAfterCredits = table.Column<int>(type: "int", nullable: false),
                    Provider = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    Feature = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    PromptTokens = table.Column<int>(type: "int", nullable: true),
                    CandidateTokens = table.Column<int>(type: "int", nullable: true),
                    ThoughtTokens = table.Column<int>(type: "int", nullable: true),
                    ToolPromptTokens = table.Column<int>(type: "int", nullable: true),
                    TotalTokens = table.Column<int>(type: "int", nullable: true),
                    AudioDurationSeconds = table.Column<int>(type: "int", nullable: true),
                    ActorUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    RelatedEntityType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    RelatedEntityId = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BudgetPeriodKey = table.Column<string>(type: "nvarchar(7)", maxLength: 7, nullable: true),
                    BudgetAmountMicros = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiCreditTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiCreditTransactions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    ClaimType = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ClaimValue = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderKey = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    RoleId = table.Column<string>(type: "nvarchar(450)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    LoginProvider = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(max)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookDocuments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    OriginalFileName = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    BlobName = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    PageCount = table.Column<int>(type: "int", nullable: false),
                    ProcessingStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessingMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    PreferredTranslationLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookDocuments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookDocuments_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Classrooms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OwnerUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    Language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    JoinCode = table.Column<string>(type: "nvarchar(8)", maxLength: 8, nullable: false),
                    JoinCodeEnabled = table.Column<bool>(type: "bit", nullable: false),
                    GroupCallId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsArchived = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Classrooms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Classrooms_AspNetUsers_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ParentCollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    OriginalCollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Collections_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Collections_Collections_ParentCollectionId",
                        column: x => x.ParentCollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationTranscripts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Stream = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationTranscripts", x => x.Id);
                    table.CheckConstraint("CK_RealtimeTranslationTranscripts_Stream", "[Stream] IN ('translation', 'source')");
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationTranscripts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookPages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PageNumber = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    ExtractionWarning = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookPages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookPages_BookDocuments_BookDocumentId",
                        column: x => x.BookDocumentId,
                        principalTable: "BookDocuments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomInvitations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    InvitedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AcceptedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomInvitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomInvitations_AspNetUsers_InvitedByUserId",
                        column: x => x.InvitedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomInvitations_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomLessons",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomLessons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomLessons_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomLessons_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomMemberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Role = table.Column<int>(type: "int", nullable: false),
                    JoinedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    LastChatReadAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomMemberships_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ClassroomMessages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    Kind = table.Column<int>(type: "int", nullable: false),
                    Body = table.Column<string>(type: "nvarchar(4000)", maxLength: 4000, nullable: false),
                    IsPinned = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    EditedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    IsDeleted = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomMessages_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomMessages_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Quizzes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CollectionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    IsSongQuiz = table.Column<bool>(type: "bit", nullable: false),
                    ProcessingStatus = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ProcessingMessage = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    SourceLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    Language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    AnkiTrackingEnabled = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackWordsForward = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackWordsReverse = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackSentencesForward = table.Column<bool>(type: "bit", nullable: false),
                    AnkiTrackSentencesReverse = table.Column<bool>(type: "bit", nullable: false),
                    IsPublic = table.Column<bool>(type: "bit", nullable: false),
                    OriginalQuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Quizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Quizzes_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Quizzes_Collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "Collections",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SourceTranscriptionDeployment = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: true),
                    BillingModel = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    CreditsPerStartedMinute = table.Column<int>(type: "int", nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TranscriptConsentAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    EndedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    LastHeartbeatAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ChargedMinutes = table.Column<int>(type: "int", nullable: false),
                    CreditsCharged = table.Column<int>(type: "int", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationSessions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationSessions_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationSessions_RealtimeTranslationTranscripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "RealtimeTranslationTranscripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationTranscriptSegments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TranscriptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false),
                    Stream = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    ProviderEventKey = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    Text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CapturedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationTranscriptSegments", x => x.Id);
                    table.CheckConstraint("CK_RealtimeTranslationTranscriptSegments_Stream", "[Stream] IN ('translation', 'source')");
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationTranscriptSegments_RealtimeTranslationTranscripts_TranscriptId",
                        column: x => x.TranscriptId,
                        principalTable: "RealtimeTranslationTranscripts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "BookPageTranslations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BookPageId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TargetLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SourceTextHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DetectedSourceLanguage = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Model = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    SegmentsJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookPageTranslations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookPageTranslations_BookPages_BookPageId",
                        column: x => x.BookPageId,
                        principalTable: "BookPages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_pending_changes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    conversation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    message_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    context_quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_pending_changes", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantPendingChanges_Quizzes_ContextQuizId",
                        column: x => x.context_quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "assistant_threads",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    context_quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    context_transcript_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    context_book_document_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    user_id = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    language = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    title = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_threads", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantThreads_AspNetUsers_UserId",
                        column: x => x.user_id,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AssistantThreads_BookDocuments_ContextBookDocumentId",
                        column: x => x.context_book_document_id,
                        principalTable: "BookDocuments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AssistantThreads_Quizzes_ContextQuizId",
                        column: x => x.context_quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AssistantThreads_Quizzes_QuizId",
                        column: x => x.quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantThreads_RealtimeTranslationTranscripts_ContextTranscriptId",
                        column: x => x.context_transcript_id,
                        principalTable: "RealtimeTranslationTranscripts",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ClassroomAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LessonId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Title = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    Instructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CreatedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomAssignments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_AspNetUsers_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_ClassroomLessons_LessonId",
                        column: x => x.LessonId,
                        principalTable: "ClassroomLessons",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomAssignments_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ClassroomContents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ContentType = table.Column<int>(type: "int", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BookDocumentId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SharedByUserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    SharedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    Note = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClassroomContents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClassroomContents_AspNetUsers_SharedByUserId",
                        column: x => x.SharedByUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomContents_BookDocuments_BookDocumentId",
                        column: x => x.BookDocumentId,
                        principalTable: "BookDocuments",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ClassroomContents_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClassroomContents_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "CustomQuizzes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    DefinitionJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    IsPlayable = table.Column<bool>(type: "bit", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomQuizzes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomQuizzes_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "quiz_sentences",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    text = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    translation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_quiz_sentences", x => x.id);
                    table.ForeignKey(
                        name: "FK_quiz_sentences_quizzes",
                        column: x => x.quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "QuizAttempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    ClassroomId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Mode = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    PracticeDirection = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    PracticeItemType = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    TotalItems = table.Column<int>(type: "int", nullable: false),
                    CorrectCount = table.Column<int>(type: "int", nullable: false),
                    IncorrectCount = table.Column<int>(type: "int", nullable: false),
                    SkippedCount = table.Column<int>(type: "int", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttempts_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Classrooms_ClassroomId",
                        column: x => x.ClassroomId,
                        principalTable: "Classrooms",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_QuizAttempts_Quizzes_QuizId",
                        column: x => x.QuizId,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "words",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(450)", nullable: false),
                    quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    lemma = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    translation = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_words", x => x.id);
                    table.ForeignKey(
                        name: "FK_words_quizzes",
                        column: x => x.quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RealtimeTranslationMinutes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MinuteIndex = table.Column<int>(type: "int", nullable: false),
                    ReservationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Credits = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    ReservedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    BegunAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RealtimeTranslationMinutes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RealtimeTranslationMinutes_RealtimeTranslationSessions_SessionId",
                        column: x => x.SessionId,
                        principalTable: "RealtimeTranslationSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "assistant_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    thread_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    context_quiz_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    role = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    content_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    pending_changes_json = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    status = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assistant_messages", x => x.id);
                    table.ForeignKey(
                        name: "FK_AssistantMessages_AssistantThreads_ThreadId",
                        column: x => x.thread_id,
                        principalTable: "assistant_threads",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AssistantMessages_Quizzes_ContextQuizId",
                        column: x => x.context_quiz_id,
                        principalTable: "Quizzes",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QuizAttemptItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuizAttemptId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Prompt = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    ExpectedAnswer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    GivenAnswer = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    IsCorrect = table.Column<bool>(type: "bit", nullable: false),
                    Sequence = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuizAttemptItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuizAttemptItems_QuizAttempts_QuizAttemptId",
                        column: x => x.QuizAttemptId,
                        principalTable: "QuizAttempts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_BudgetPeriodKey",
                table: "AiCreditTransactions",
                column: "BudgetPeriodKey");

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_ReservationId",
                table: "AiCreditTransactions",
                column: "ReservationId");

            migrationBuilder.CreateIndex(
                name: "IX_AiCreditTransactions_UserId_CreatedAt",
                table: "AiCreditTransactions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true,
                filter: "[NormalizedName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true,
                filter: "[NormalizedUserName] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_messages_context_quiz_id",
                table: "assistant_messages",
                column: "context_quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_messages_thread_id_sequence",
                table: "assistant_messages",
                columns: new[] { "thread_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_context_quiz_id",
                table: "assistant_pending_changes",
                column: "context_quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_conversation_id_sequence",
                table: "assistant_pending_changes",
                columns: new[] { "conversation_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_message_id",
                table: "assistant_pending_changes",
                column: "message_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_pending_changes_user_id_status",
                table: "assistant_pending_changes",
                columns: new[] { "user_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_context_book_document_id",
                table: "assistant_threads",
                column: "context_book_document_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_context_quiz_id",
                table: "assistant_threads",
                column: "context_quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_context_transcript_id",
                table: "assistant_threads",
                column: "context_transcript_id");

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_quiz_id_user_id",
                table: "assistant_threads",
                columns: new[] { "quiz_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "IX_assistant_threads_user_id_quiz_id_language",
                table: "assistant_threads",
                columns: new[] { "user_id", "quiz_id", "language" });

            migrationBuilder.CreateIndex(
                name: "IX_BookDocuments_UserId",
                table: "BookDocuments",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_BookDocuments_UserId_CreatedAt",
                table: "BookDocuments",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_BookDocuments_UserId_Language",
                table: "BookDocuments",
                columns: new[] { "UserId", "Language" });

            migrationBuilder.CreateIndex(
                name: "IX_BookPages_BookDocumentId_PageNumber",
                table: "BookPages",
                columns: new[] { "BookDocumentId", "PageNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookPageTranslations_BookPageId_TargetLanguage_SourceTextHash_SchemaVersion",
                table: "BookPageTranslations",
                columns: new[] { "BookPageId", "TargetLanguage", "SourceTextHash", "SchemaVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_ClassroomId_DueAt",
                table: "ClassroomAssignments",
                columns: new[] { "ClassroomId", "DueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_CreatedByUserId",
                table: "ClassroomAssignments",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_LessonId",
                table: "ClassroomAssignments",
                column: "LessonId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomAssignments_QuizId",
                table: "ClassroomAssignments",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_BookDocumentId",
                table: "ClassroomContents",
                column: "BookDocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_ClassroomId_BookDocumentId",
                table: "ClassroomContents",
                columns: new[] { "ClassroomId", "BookDocumentId" },
                unique: true,
                filter: "[BookDocumentId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_ClassroomId_QuizId",
                table: "ClassroomContents",
                columns: new[] { "ClassroomId", "QuizId" },
                unique: true,
                filter: "[QuizId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_ClassroomId_SharedAt",
                table: "ClassroomContents",
                columns: new[] { "ClassroomId", "SharedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_QuizId",
                table: "ClassroomContents",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomContents_SharedByUserId",
                table: "ClassroomContents",
                column: "SharedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomInvitations_ClassroomId_Email",
                table: "ClassroomInvitations",
                columns: new[] { "ClassroomId", "Email" },
                unique: true,
                filter: "[AcceptedAt] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomInvitations_Email",
                table: "ClassroomInvitations",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomInvitations_InvitedByUserId",
                table: "ClassroomInvitations",
                column: "InvitedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomLessons_ClassroomId_ScheduledAt",
                table: "ClassroomLessons",
                columns: new[] { "ClassroomId", "ScheduledAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomLessons_CreatedByUserId",
                table: "ClassroomLessons",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMemberships_ClassroomId_UserId",
                table: "ClassroomMemberships",
                columns: new[] { "ClassroomId", "UserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMemberships_UserId",
                table: "ClassroomMemberships",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMessages_ClassroomId_Kind_CreatedAt",
                table: "ClassroomMessages",
                columns: new[] { "ClassroomId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ClassroomMessages_UserId",
                table: "ClassroomMessages",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_JoinCode",
                table: "Classrooms",
                column: "JoinCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Classrooms_OwnerUserId",
                table: "Classrooms",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_IsPublic_Language",
                table: "Collections",
                columns: new[] { "IsPublic", "Language" });

            migrationBuilder.CreateIndex(
                name: "IX_Collections_OriginalCollectionId",
                table: "Collections",
                column: "OriginalCollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_ParentCollectionId",
                table: "Collections",
                column: "ParentCollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Collections_UserId_Language_ParentCollectionId_Name",
                table: "Collections",
                columns: new[] { "UserId", "Language", "ParentCollectionId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomQuizzes_QuizId_IsPlayable",
                table: "CustomQuizzes",
                columns: new[] { "QuizId", "IsPlayable" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomQuizzes_QuizId_Name",
                table: "CustomQuizzes",
                columns: new[] { "QuizId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_quiz_sentences_quiz_id",
                table: "quiz_sentences",
                column: "quiz_id");

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttemptItems_QuizAttemptId_Sequence",
                table: "QuizAttemptItems",
                columns: new[] { "QuizAttemptId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_ClassroomId_QuizId_CompletedAt",
                table: "QuizAttempts",
                columns: new[] { "ClassroomId", "QuizId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_QuizId",
                table: "QuizAttempts",
                column: "QuizId");

            migrationBuilder.CreateIndex(
                name: "IX_QuizAttempts_UserId_CompletedAt",
                table: "QuizAttempts",
                columns: new[] { "UserId", "CompletedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Quizzes_CollectionId",
                table: "Quizzes",
                column: "CollectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Quizzes_UserId",
                table: "Quizzes",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationMinutes_ReservationId",
                table: "RealtimeTranslationMinutes",
                column: "ReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationMinutes_SessionId_MinuteIndex",
                table: "RealtimeTranslationMinutes",
                columns: new[] { "SessionId", "MinuteIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationSessions_TranscriptId",
                table: "RealtimeTranslationSessions",
                column: "TranscriptId");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationSessions_UserId",
                table: "RealtimeTranslationSessions",
                column: "UserId",
                unique: true,
                filter: "[Status] IN ('pending', 'active')");

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationSessions_UserId_CreatedAt",
                table: "RealtimeTranslationSessions",
                columns: new[] { "UserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscripts_UserId_TargetLanguage_UpdatedAt",
                table: "RealtimeTranslationTranscripts",
                columns: new[] { "UserId", "TargetLanguage", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Stream_ProviderEventKey",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "SessionId", "Stream", "ProviderEventKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_SessionId_Stream_Sequence",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "SessionId", "Stream", "Sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_TranscriptId_CapturedAt",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "TranscriptId", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_RealtimeTranslationTranscriptSegments_TranscriptId_Stream_CapturedAt",
                table: "RealtimeTranslationTranscriptSegments",
                columns: new[] { "TranscriptId", "Stream", "CapturedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_words_quiz_id",
                table: "words",
                column: "quiz_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AcsUserIdentities");

            migrationBuilder.DropTable(
                name: "AiCreditAccounts");

            migrationBuilder.DropTable(
                name: "AiCreditTransactions");

            migrationBuilder.DropTable(
                name: "AiMonthlyBudgets");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "assistant_messages");

            migrationBuilder.DropTable(
                name: "assistant_pending_changes");

            migrationBuilder.DropTable(
                name: "BookPageTranslations");

            migrationBuilder.DropTable(
                name: "ClassroomAssignments");

            migrationBuilder.DropTable(
                name: "ClassroomContents");

            migrationBuilder.DropTable(
                name: "ClassroomInvitations");

            migrationBuilder.DropTable(
                name: "ClassroomMemberships");

            migrationBuilder.DropTable(
                name: "ClassroomMessages");

            migrationBuilder.DropTable(
                name: "CustomQuizzes");

            migrationBuilder.DropTable(
                name: "quiz_sentences");

            migrationBuilder.DropTable(
                name: "QuizAttemptItems");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationMinutes");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationTranscriptSegments");

            migrationBuilder.DropTable(
                name: "words");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "assistant_threads");

            migrationBuilder.DropTable(
                name: "BookPages");

            migrationBuilder.DropTable(
                name: "ClassroomLessons");

            migrationBuilder.DropTable(
                name: "QuizAttempts");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationSessions");

            migrationBuilder.DropTable(
                name: "BookDocuments");

            migrationBuilder.DropTable(
                name: "Classrooms");

            migrationBuilder.DropTable(
                name: "Quizzes");

            migrationBuilder.DropTable(
                name: "RealtimeTranslationTranscripts");

            migrationBuilder.DropTable(
                name: "Collections");

            migrationBuilder.DropTable(
                name: "AspNetUsers");
        }
    }
}
