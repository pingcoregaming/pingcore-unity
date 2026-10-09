using System;
using UnityEditor;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Common
{
    /// <summary>Small builders the PingCore windows share, so every page looks and behaves alike.</summary>
    public static class Ui
    {
        /// <summary>The package's style sheet.</summary>
        public const string StyleSheetPath = "Packages/io.pingcore.editor/Editor/Workspace/UI/Common/PingCoreEditor.uss";

        /// <summary>Adds the PingCore style sheet (when it loads) and the page padding.</summary>
        public static VisualElement Page(VisualElement root)
        {
            StyleSheet sheet = AssetDatabase.LoadAssetAtPath<StyleSheet>(StyleSheetPath);
            if (sheet != null)
            {
                root.styleSheets.Add(sheet);
            }

            root.AddToClassList("pingcore-page");
            return root;
        }

        public static Label Title(string text) => WithClass(new Label(text), "pingcore-title");

        public static Label Section(string text) => WithClass(new Label(text), "pingcore-section");

        public static Label Note(string text) => WithClass(new Label(text), "pingcore-note");

        public static Label Status(string text = "") => WithClass(new Label(text), "pingcore-status");

        /// <summary>A banner whose kind (info, warning, error) can be changed later with <see cref="SetBanner"/>.</summary>
        public static Label Banner(string text, BannerKind kind)
        {
            var label = WithClass(new Label(), "pingcore-banner");
            SetBanner(label, text, kind);
            return label;
        }

        /// <summary>Sets a banner's text and kind; an empty text hides it.</summary>
        public static void SetBanner(Label banner, string text, BannerKind kind)
        {
            banner.text = text ?? string.Empty;
            banner.RemoveFromClassList("pingcore-banner--info");
            banner.RemoveFromClassList("pingcore-banner--warning");
            banner.RemoveFromClassList("pingcore-banner--error");
            banner.AddToClassList(kind == BannerKind.Warning ? "pingcore-banner--warning" : kind == BannerKind.Error ? "pingcore-banner--error" : "pingcore-banner--info");
            banner.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        public static VisualElement Row(params VisualElement[] children)
        {
            var row = WithClass(new VisualElement(), "pingcore-row");
            foreach (VisualElement child in children)
            {
                row.Add(child);
            }

            return row;
        }

        public static Button Button(string text, Action onClick) => new Button(onClick) { text = text };

        /// <summary>Marks a section's one next action.</summary>
        public static Button Primary(Button button) => WithClass(button, "pingcore-primary");

        public static TextField Text(string label, string value = "")
        {
            var field = new TextField(label) { value = value ?? string.Empty };
            field.AddToClassList("pingcore-grow");
            return field;
        }

        /// <summary>A password field: shows dots, never the value.</summary>
        public static TextField Password(string label)
        {
            var field = new TextField(label) { isPasswordField = true, maskChar = '*' };
            field.AddToClassList("pingcore-grow");
            return field;
        }

        public static T WithClass<T>(T element, string className)
            where T : VisualElement
        {
            element.AddToClassList(className);
            return element;
        }
    }

    /// <summary>The kind of a banner.</summary>
    public enum BannerKind
    {
        Info,
        Warning,
        Error,
    }
}
