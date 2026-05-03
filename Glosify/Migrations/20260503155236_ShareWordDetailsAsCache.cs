using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glosify.Migrations
{
    /// <inheritdoc />
    public partial class ShareWordDetailsAsCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF OBJECT_ID(N'[dbo].[word_details]', N'U') IS NOT NULL
                    AND COL_LENGTH(N'[dbo].[word_details]', N'quiz_id') IS NOT NULL
                BEGIN
                    DECLARE @dropSql nvarchar(max) = N'';

                    SELECT @dropSql += N'ALTER TABLE '
                        + QUOTENAME(OBJECT_SCHEMA_NAME(parent_object_id))
                        + N'.'
                        + QUOTENAME(OBJECT_NAME(parent_object_id))
                        + N' DROP CONSTRAINT '
                        + QUOTENAME(name)
                        + N';'
                    FROM sys.foreign_keys
                    WHERE referenced_object_id = OBJECT_ID(N'[dbo].[word_details]');

                    IF @dropSql <> N''
                    BEGIN
                        EXEC sp_executesql @dropSql;
                    END

                    IF OBJECT_ID(N'tempdb..#WordDetailMap') IS NOT NULL
                    BEGIN
                        DROP TABLE #WordDetailMap;
                    END

                    SELECT
                        w.quiz_id,
                        w.word_detail_id AS old_word_detail_id,
                        CONCAT(
                            N'wd:',
                            LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', LOWER(TRIM(q.SourceLanguage))), 2)),
                            N':',
                            LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', LOWER(TRIM(q.TargetLanguage))), 2)),
                            N':',
                            LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', LOWER(TRIM(w.lemma))), 2)),
                            N':',
                            LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', LOWER(TRIM(w.translation))), 2))
                        ) AS new_word_detail_id,
                        q.SourceLanguage AS source_language,
                        q.TargetLanguage AS target_language,
                        w.lemma AS word,
                        w.translation,
                        LOWER(TRIM(w.lemma)) AS normalized_word,
                        LOWER(TRIM(w.translation)) AS normalized_translation,
                        LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', LOWER(TRIM(w.lemma))), 2)) AS normalized_word_hash,
                        LOWER(CONVERT(varchar(64), HASHBYTES('SHA2_256', LOWER(TRIM(w.translation))), 2)) AS normalized_translation_hash,
                        COALESCE(NULLIF(d.properties, N''), N'{}') AS properties,
                        COALESCE(NULLIF(d.example_sentence, N''), N'') AS example_sentence,
                        COALESCE(NULLIF(d.explanation, N''), N'') AS explanation,
                        COALESCE(NULLIF(d.variants, N''), N'[]') AS variants,
                        COALESCE(NULLIF(d.language, N''), q.TargetLanguage) AS language
                    INTO #WordDetailMap
                    FROM dbo.words w
                    INNER JOIN dbo.Quizzes q ON q.Id = w.quiz_id
                    LEFT JOIN dbo.word_details d
                        ON d.quiz_id = w.quiz_id
                        AND d.id = w.word_detail_id;

                    CREATE TABLE dbo.word_details_shared (
                        id nvarchar(450) NOT NULL,
                        source_language nvarchar(64) NOT NULL,
                        target_language nvarchar(64) NOT NULL,
                        word nvarchar(256) NOT NULL,
                        translation nvarchar(512) NOT NULL,
                        normalized_word nvarchar(1024) NOT NULL,
                        normalized_translation nvarchar(1024) NOT NULL,
                        normalized_word_hash nvarchar(64) NOT NULL,
                        normalized_translation_hash nvarchar(64) NOT NULL,
                        properties nvarchar(max) NOT NULL,
                        example_sentence nvarchar(max) NOT NULL,
                        explanation nvarchar(max) NOT NULL,
                        variants nvarchar(max) NOT NULL,
                        language nvarchar(max) NOT NULL,
                        created_at datetimeoffset NOT NULL,
                        updated_at datetimeoffset NOT NULL,
                        CONSTRAINT PK_word_details_shared PRIMARY KEY (id),
                        CONSTRAINT CK_word_details_shared_properties_json CHECK (ISJSON(properties) = 1),
                        CONSTRAINT CK_word_details_shared_variants_json CHECK (ISJSON(variants) = 1)
                    );

                    ;WITH ranked AS (
                        SELECT
                            *,
                            ROW_NUMBER() OVER (
                                PARTITION BY new_word_detail_id
                                ORDER BY
                                    CASE WHEN properties <> N'{}' THEN 0 ELSE 1 END,
                                    CASE WHEN variants <> N'[]' THEN 0 ELSE 1 END,
                                    CASE WHEN explanation <> N'' THEN 0 ELSE 1 END,
                                    CASE WHEN example_sentence <> N'' THEN 0 ELSE 1 END
                            ) AS rn
                        FROM #WordDetailMap
                    )
                    INSERT INTO dbo.word_details_shared (
                        id,
                        source_language,
                        target_language,
                        word,
                        translation,
                        normalized_word,
                        normalized_translation,
                        normalized_word_hash,
                        normalized_translation_hash,
                        properties,
                        example_sentence,
                        explanation,
                        variants,
                        language,
                        created_at,
                        updated_at
                    )
                    SELECT
                        new_word_detail_id,
                        source_language,
                        target_language,
                        LEFT(word, 256),
                        LEFT(translation, 512),
                        normalized_word,
                        normalized_translation,
                        normalized_word_hash,
                        normalized_translation_hash,
                        properties,
                        example_sentence,
                        explanation,
                        variants,
                        language,
                        SYSDATETIMEOFFSET(),
                        SYSDATETIMEOFFSET()
                    FROM ranked
                    WHERE rn = 1;

                    UPDATE w
                    SET word_detail_id = m.new_word_detail_id
                    FROM dbo.words w
                    INNER JOIN #WordDetailMap m
                        ON m.quiz_id = w.quiz_id
                        AND m.old_word_detail_id = w.word_detail_id;

                    DROP TABLE dbo.word_details;
                    EXEC sp_rename N'dbo.word_details_shared', N'word_details';
                    EXEC sp_rename N'dbo.PK_word_details_shared', N'PK_word_details', N'OBJECT';
                    EXEC sp_rename N'dbo.CK_word_details_shared_properties_json', N'CK_word_details_properties_json', N'OBJECT';
                    EXEC sp_rename N'dbo.CK_word_details_shared_variants_json', N'CK_word_details_variants_json', N'OBJECT';

                    CREATE UNIQUE INDEX IX_word_details_source_language_target_language_normalized_word_hash_normalized_translation_hash
                        ON dbo.word_details (
                            source_language,
                            target_language,
                            normalized_word_hash,
                            normalized_translation_hash
                        );

                    DROP TABLE #WordDetailMap;
                END

                IF OBJECT_ID(N'[dbo].[word_detail_cache]', N'U') IS NOT NULL
                BEGIN
                    DROP TABLE dbo.word_detail_cache;
                END

                IF OBJECT_ID(N'[dbo].[words]', N'U') IS NOT NULL
                    AND COL_LENGTH(N'[dbo].[words]', N'word_detail_id') IS NOT NULL
                    AND EXISTS (
                        SELECT 1
                        FROM sys.columns
                        WHERE object_id = OBJECT_ID(N'[dbo].[words]')
                            AND name = N'word_detail_id'
                            AND max_length = -1
                    )
                BEGIN
                    ALTER TABLE dbo.words ALTER COLUMN word_detail_id nvarchar(450) NOT NULL;
                END

                IF OBJECT_ID(N'[dbo].[words]', N'U') IS NOT NULL
                    AND COL_LENGTH(N'[dbo].[words]', N'word_detail_id') IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM sys.indexes
                        WHERE object_id = OBJECT_ID(N'[dbo].[words]')
                            AND name = N'IX_words_word_detail_id'
                    )
                BEGIN
                    CREATE INDEX IX_words_word_detail_id ON dbo.words(word_detail_id);
                END

                IF OBJECT_ID(N'[dbo].[words]', N'U') IS NOT NULL
                    AND OBJECT_ID(N'[dbo].[word_details]', N'U') IS NOT NULL
                    AND NOT EXISTS (
                        SELECT 1
                        FROM sys.foreign_keys
                        WHERE name = N'FK_words_word_details'
                            AND parent_object_id = OBJECT_ID(N'[dbo].[words]')
                    )
                BEGIN
                    ALTER TABLE dbo.words
                    ADD CONSTRAINT FK_words_word_details
                    FOREIGN KEY (word_detail_id)
                    REFERENCES dbo.word_details(id);
                END

                IF OBJECT_ID(N'[dbo].[words]', N'U') IS NOT NULL
                    AND EXISTS (
                        SELECT 1
                        FROM sys.key_constraints kc
                        INNER JOIN sys.index_columns ic
                            ON ic.object_id = kc.parent_object_id
                            AND ic.index_id = kc.unique_index_id
                        INNER JOIN sys.columns c
                            ON c.object_id = ic.object_id
                            AND c.column_id = ic.column_id
                        WHERE kc.parent_object_id = OBJECT_ID(N'[dbo].[words]')
                            AND kc.[type] = 'PK'
                            AND c.name = N'quiz_id'
                    )
                BEGIN
                    ALTER TABLE dbo.words DROP CONSTRAINT PK_words;
                    ALTER TABLE dbo.words ADD CONSTRAINT PK_words PRIMARY KEY (id);
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Shared word details intentionally replace the old quiz-scoped layout. Reversing would
            // require fabricating per-quiz detail rows and is not safe as an automatic migration.
        }
    }
}
