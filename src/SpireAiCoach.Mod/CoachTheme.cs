using Godot;

namespace SpireAiCoach.Mod;

internal static class CoachTheme
{
    public static readonly Color Text = new("e5edf4");
    public static readonly Color Muted = new("a3b6c8");
    public static readonly Color Gold = new("e5c38a");

    private static StyleBoxFlat Box(string fill, string border, int margin = 10) => new()
    {
        BgColor = new Color(fill), BorderColor = new Color(border),
        BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 1,
        CornerRadiusTopLeft = 8, CornerRadiusTopRight = 8,
        CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8,
        ContentMarginLeft = margin, ContentMarginRight = margin,
        ContentMarginTop = 8, ContentMarginBottom = 8
    };

    public static Theme Create()
    {
        var theme = new Theme
        {
            DefaultFont = new SystemFont { FontNames = ["Microsoft YaHei UI", "Microsoft YaHei", "Noto Sans CJK SC"] },
            DefaultFontSize = 16
        };
        foreach (var type in new[] { "Label", "Button", "CheckBox", "OptionButton", "LineEdit", "TextEdit" })
            theme.SetColor("font_color", type, Text);
        theme.SetColor("default_color", "RichTextLabel", Text);
        theme.SetColor("font_disabled_color", "Button", new Color("8393a4"));
        theme.SetColor("font_hover_color", "Button", new Color("ffffff"));
        theme.SetColor("font_pressed_color", "Button", new Color("ffffff"));
        theme.SetStylebox("normal", "Button", Box("253649", "3f556c"));
        theme.SetStylebox("hover", "Button", Box("314b61", "7593aa"));
        theme.SetStylebox("pressed", "Button", Box("1d4c52", "62b4b2"));
        theme.SetStylebox("disabled", "Button", Box("202a38", "354252"));
        foreach (var state in new[] { "normal", "hover", "pressed", "hover_pressed", "disabled" })
            theme.SetStylebox(state, "CheckBox", new StyleBoxEmpty
            { ContentMarginLeft = 4, ContentMarginRight = 4, ContentMarginTop = 2, ContentMarginBottom = 2 });
        theme.SetStylebox("focus", "Button", new StyleBoxFlat
        {
            DrawCenter = false, BorderColor = Gold, BorderWidthLeft = 2, BorderWidthRight = 2,
            BorderWidthTop = 2, BorderWidthBottom = 2, CornerRadiusTopLeft = 8,
            CornerRadiusTopRight = 8, CornerRadiusBottomLeft = 8, CornerRadiusBottomRight = 8
        });
        foreach (var type in new[] { "LineEdit", "TextEdit" })
        {
            theme.SetStylebox("normal", type, Box("111b27", "3b5065"));
            theme.SetStylebox("focus", type, Box("14232e", "62b4b2"));
            theme.SetStylebox("read_only", type, Box("111b27", "303f51"));
        }
        theme.SetStylebox("background", "ProgressBar", Box("111b27", "303f51", 0));
        theme.SetStylebox("fill", "ProgressBar", Box("4d9694", "4d9694", 0));
        theme.SetConstant("separation", "VBoxContainer", 8);
        theme.SetConstant("h_separation", "GridContainer", 8);
        theme.SetConstant("v_separation", "GridContainer", 8);
        theme.SetStylebox("panel", "PanelContainer", Box("111b27f5", "665d49", 14));
        return theme;
    }

    public static VBoxContainer Section(VBoxContainer parent, string title)
    {
        var card = new PanelContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddThemeStyleboxOverride("panel", Box("1c2936", "354558", 12));
        var content = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        card.AddChild(content); parent.AddChild(card);
        var heading = new Label { Text = title };
        heading.AddThemeColorOverride("font_color", Gold);
        heading.AddThemeFontSizeOverride("font_size", 17);
        content.AddChild(heading);
        return content;
    }

    public static Button Disclosure(string title, Control content)
    {
        var button = new Button { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill, Alignment = HorizontalAlignment.Left };
        void Refresh() => button.Text = (content.Visible ? "▾  " : "▸  ") + title;
        button.Pressed += () => content.Visible = !content.Visible;
        content.VisibilityChanged += Refresh;
        Refresh(); return button;
    }

    public static void Accent(Button button)
    {
        button.AddThemeStyleboxOverride("normal", Box("255557", "5ca5a2"));
        button.AddThemeStyleboxOverride("hover", Box("316d70", "93d2cb"));
    }
}
