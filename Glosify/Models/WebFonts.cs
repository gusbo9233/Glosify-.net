namespace Glosify.Models;

/// <summary>
/// The Google Fonts stylesheets the layouts link, kept in one place so the app
/// layout and the auth layout cannot drift apart.
/// </summary>
public static class WebFonts
{
    /// <summary>
    /// Every Material Symbols ligature the application renders.
    /// </summary>
    /// <remarks>
    /// The full Material Symbols Outlined font is 1.1 MB — more than the rest of
    /// a page put together, for around a hundred icons. Naming the icons makes
    /// Google serve a subset instead, which is ~38 KB. The cost is that an icon
    /// missing from this list renders as its own name in plain text, so
    /// MaterialSymbolInventoryTests scans the views and scripts and fails the
    /// build when a newly used icon has not been added here. Keep it sorted.
    /// </remarks>
    public static readonly string[] IconNames =
    [
        "account_balance_wallet", "account_circle", "account_tree", "add", "add_circle",
        "add_home_work", "arrow_back", "arrow_forward", "arrow_outward", "arrow_upward", "assignment",
        "assignment_add", "auto_awesome", "auto_fix_high", "auto_stories", "backspace", "bakery_dining", "bolt",
        "calendar_today", "call_end", "campaign", "chat_bubble", "check", "check_circle", "checklist",
        "chevron_left", "chevron_right", "close", "cloud", "content_copy", "create_new_folder",
        "dashboard_customize", "data_object", "delete", "description", "diversity_3", "drag_indicator", "east", "edit",
        "edit_note", "error", "event", "explore", "fit_screen", "flag", "folder", "folder_open",
        "folder_shared", "format_align_left", "format_quote", "forum", "graphic_eq", "grid_4x4",
        "group", "groups", "hourglass_top", "info", "inventory_2", "keep", "key", "keyboard",
        "library_add", "library_books", "local_bar", "lock", "lock_reset", "login", "logout",
        "mark_email_unread", "menu_book", "mic", "mic_off", "more_vert", "neurology", "north_east", "notes",
        "notifications", "outgoing_mail", "person", "person_add", "person_search", "picture_as_pdf",
        "play_circle", "psychology", "public", "quiz", "remove", "replay",
        "rotate_right", "school", "science", "search", "search_off", "send", "settings", "share", "short_text",
        "shot_bar", "stacks", "style", "subject", "subtitles", "subtitles_off", "swap_horiz",
        "task_alt", "thumb_down", "thumb_up", "timer", "translate", "travel_explore", "tune", "undo", "upload_file", "videocam",
        "videocam_off", "view_column_2", "volume_up", "waving_hand", "west",
    ];

    /// <summary>
    /// Body and display faces. Both are linked from the head rather than
    /// imported from site.css, so the browser can start them immediately instead
    /// of waiting for a stylesheet to arrive first. `display=swap` keeps text
    /// readable in a fallback face while they load.
    /// </summary>
    public const string TextStylesheetUrl =
        "https://fonts.googleapis.com/css2"
        + "?family=Lora:ital,wght@0,400..700;1,400..700"
        + "&family=Plus+Jakarta+Sans:ital,wght@0,200..800;1,400..800"
        + "&display=swap";

    /// <summary>
    /// The icon subset. Unlike the text faces this uses `display=block`: an icon
    /// font swapping in late would otherwise flash each ligature as its literal
    /// name ("notifications", "search") and then reflow around it.
    /// </summary>
    /// <remarks>
    /// The axis ranges match the two variations site.css asks for — 'wght' 400
    /// and 500, 'FILL' 0 and 1 — at the fixed 'opsz' 24 and 'GRAD' 0 it uses
    /// everywhere. Google requires axes in this order and icon names sorted.
    /// </remarks>
    public static readonly string IconStylesheetUrl =
        "https://fonts.googleapis.com/css2"
        + "?family=Material+Symbols+Outlined:opsz,wght,FILL,GRAD@24,400..500,0..1,0"
        + $"&icon_names={string.Join(',', IconNames.Order(StringComparer.Ordinal))}"
        + "&display=block";
}
