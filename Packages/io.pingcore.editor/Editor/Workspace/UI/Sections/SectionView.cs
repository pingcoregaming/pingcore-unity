using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PingCore.Editor.Workspace.Credentials;
using PingCore.Editor.Workspace.UI.Common;
using UnityEngine;
using UnityEngine.UIElements;

namespace PingCore.Editor.Workspace.UI.Sections
{
    /// <summary>
    /// One section on the page: the header (click to fold) with its status chip, the line that says what the
    /// section is, the line that says where it stands and its one next action, and the body. A blocked section
    /// is folded and its body disabled; otherwise a section unfolds when it becomes "to do" and folds when it
    /// becomes "done", and the developer may fold it either way in between. Player hosting starts folded and
    /// stays as the developer leaves it.
    /// </summary>
    public sealed class SectionView
    {
        private readonly Label header;
        private readonly Label chip;
        private readonly Label state;
        private readonly bool optional;
        private SectionStatus? shownStatus;
        private bool expanded;

        public SectionView(Section section, bool optional = false)
        {
            Section = section;
            this.optional = optional;
            expanded = !optional;
            Root = Ui.WithClass(new VisualElement(), "pingcore-card");
            header = Ui.WithClass(new Label(), "pingcore-card__title");
            header.RegisterCallback<ClickEvent>(_ => Expanded = !Expanded);
            chip = Ui.WithClass(new Label(), "pingcore-chip");
            Root.Add(Ui.WithClass(Ui.Row(header, chip), "pingcore-card__header"));
            Root.Add(Ui.Note(SectionBook.What(section)));
            state = Ui.WithClass(new Label(), "pingcore-card__state");
            Root.Add(state);
            Body = Ui.WithClass(new VisualElement(), "pingcore-card__body");
            Root.Add(Body);
            Apply(new SectionState(section, optional ? SectionStatus.Off : SectionStatus.ToDo, string.Empty, null));
        }

        public Section Section { get; }

        public VisualElement Root { get; }

        /// <summary>Where the section's controls go.</summary>
        public VisualElement Body { get; }

        /// <summary>The state on show.</summary>
        public SectionState State { get; private set; }

        /// <summary>Whether the body shows. A blocked section stays folded.</summary>
        public bool Expanded
        {
            get => expanded;
            set
            {
                expanded = value && (State == null || State.Enabled);
                Body.style.display = expanded ? DisplayStyle.Flex : DisplayStyle.None;
                header.text = (expanded ? "▾ " : "▸ ") + SectionBook.Title(Section);
            }
        }

        /// <summary>Shows <paramref name="next"/>: chip, line, next action, enabled state and the fold rule.</summary>
        public void Apply(SectionState next)
        {
            State = next;
            chip.text = next.Chip;
            chip.EnableInClassList("pingcore-chip--done", next.Status == SectionStatus.Done || next.Status == SectionStatus.Ready);
            chip.EnableInClassList("pingcore-chip--todo", next.Status == SectionStatus.ToDo);
            chip.EnableInClassList("pingcore-chip--blocked", next.Status == SectionStatus.Blocked || next.Status == SectionStatus.Unavailable || next.Status == SectionStatus.Off);
            state.text = next.NextAction == null ? next.Line : next.Line + " Next: " + next.NextAction;
            Body.SetEnabled(next.Enabled);
            if (shownStatus != next.Status && !optional)
            {
                shownStatus = next.Status;
                Expanded = next.Status != SectionStatus.Done;
            }
            else
            {
                shownStatus = next.Status;
                Expanded = expanded;
            }
        }
    }

    /// <summary>
    /// How a section runs a button's work: a second press while a call is in flight says so, and every failure
    /// becomes a sentence in the section's status line, never a dialog and never a secret (messages name files
    /// and exception types only).
    /// </summary>
    public sealed class SectionActions
    {
        private bool busy;

        /// <summary>True while a press is still running; one-shot reads wait for the next refresh instead of being refused.</summary>
        public bool Busy => busy;

        /// <summary>Runs <paramref name="action"/>, then <paramref name="after"/> (the window's refresh) whatever happened.</summary>
        public async void Run(Label status, Func<CancellationToken, Task> action, Action after = null)
        {
            if (busy)
            {
                status.text = "Still working on the last request; wait for its answer.";
                return;
            }

            busy = true;
            try
            {
                await action(CancellationToken.None);
            }
            catch (Exception e)
            {
                status.text = Explain(e);
                if (!(e is InvalidOperationException || e is InvalidDataException || e is CredentialStoreException || e is IOException))
                {
                    Debug.LogWarning("[PingCore] unexpected " + e.GetType().Name);
                }
            }
            finally
            {
                busy = false;
                after?.Invoke();
            }
        }

        /// <summary>Runs a synchronous action, turning a settings or store failure into a sentence.</summary>
        public static void Guard(Label status, Action action, Action after = null)
        {
            try
            {
                action();
            }
            catch (Exception e) when (e is InvalidOperationException || e is InvalidDataException || e is CredentialStoreException || e is IOException || e is ArgumentException)
            {
                status.text = Explain(e);
            }
            finally
            {
                after?.Invoke();
            }
        }

        private static string Explain(Exception e)
        {
            switch (e)
            {
                case InvalidDataException data:
                    return data.Message + " Nothing was changed.";
                case InvalidOperationException op:
                    return op.Message;
                case CredentialStoreException store:
                    return "The credential store failed: " + store.Message;
                case IOException io:
                    return "A settings file could not be written (" + io.GetType().Name + ").";
                case ArgumentException arg:
                    return arg.Message;
                default:
                    return "Unexpected " + e.GetType().Name + "; nothing was stored.";
            }
        }
    }
}
