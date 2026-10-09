using System;
using System.Collections.Generic;
using BeaconRush.Client.Models;
using BeaconRush.Match;
using UnityEngine.UIElements;

namespace BeaconRush.Client.UI.Views
{
    /// <summary>Small helpers every view uses: required element lookups, showing and hiding, and the coloured score rows.</summary>
    internal static class Ui
    {
        /// <summary>The element called <paramref name="name"/> under <paramref name="root"/>; a missing one is a broken layout, so it throws.</summary>
        public static T Require<T>(VisualElement root, string name) where T : VisualElement
        {
            T found = root.Q<T>(name);
            if (found == null)
            {
                throw new InvalidOperationException("Layout/ClientMenu.uxml has no " + typeof(T).Name + " named " + name);
            }

            return found;
        }

        /// <summary>Shows or hides an element (display flex or none).</summary>
        public static void Show(VisualElement element, bool visible)
        {
            DisplayStyle wanted = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (element.style.display != wanted)
            {
                element.style.display = wanted;
            }
        }

        /// <summary>Sets a label's text only when it changed.</summary>
        public static void Text(TextElement element, string text)
        {
            text = text ?? string.Empty;
            if (element.text != text)
            {
                element.text = text;
            }
        }

        /// <summary>Enables or disables an element only when it changed.</summary>
        public static void Enable(VisualElement element, bool enabled)
        {
            if (element.enabledSelf != enabled)
            {
                element.SetEnabled(enabled);
            }
        }

        /// <summary>
        /// Keeps <paramref name="list"/> showing one row per entry (a colour swatch by join order, the name, the score), this
        /// player's row marked. Rows are reused; only their text and colour change from frame to frame.
        /// </summary>
        public static void ScoreRows(VisualElement list, IReadOnlyList<LobbyEntry> entries)
        {
            while (list.childCount > entries.Count)
            {
                list.RemoveAt(list.childCount - 1);
            }

            while (list.childCount < entries.Count)
            {
                var row = new VisualElement();
                row.AddToClassList("br-score-row");
                var swatch = new VisualElement();
                swatch.AddToClassList("br-swatch");
                var name = new Label();
                name.AddToClassList("br-score-name");
                var score = new Label();
                score.AddToClassList("br-score-value");
                row.Add(swatch);
                row.Add(name);
                row.Add(score);
                list.Add(row);
            }

            List<ulong> ids = ClientUi.Ids(entries);
            for (int i = 0; i < entries.Count; i++)
            {
                LobbyEntry entry = entries[i];
                VisualElement row = list[i];
                row.EnableInClassList("br-local", entry.IsLocal);
                var colour = (UnityEngine.Color)PlayerPalette.Colour(PlayerPalette.SlotFor(entry.ClientId, ids));
                if (row[0].style.backgroundColor != colour)
                {
                    row[0].style.backgroundColor = colour;
                }

                Text((Label)row[1], entry.IsLocal ? entry.Name + " (you)" : entry.Name);
                Text((Label)row[2], entry.Score.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
