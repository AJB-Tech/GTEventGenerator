﻿using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.Drawing;

using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Windows.Input;

using Microsoft.Win32;

using Humanizer;
using FastDeepCloner;
using DiscordRPC;

using GTEventGenerator.Entities;
using GTEventGenerator.Utils;
using GTEventGenerator.Database;
using GTEventGenerator.PDUtils;

namespace GTEventGenerator
{
    public partial class GameGeneratorWindow : Window
    {
        private Random _random = new Random();
        private System.Drawing.Image _eventImage;
        private GameDB GameDatabase;
        private MenuDB MenuDB;
        private SQLiteDataReader results;
        public GameParameter GameParameter { get; set; }
        public Event CurrentEvent { get; set; }

        public LocalSettings Settings { get; set; }
        public DiscordRpcClient Client { get; private set; }

        bool menuDBValid = false, validationErrors = false;
        string selectedPath = "";

        public const int BaseEventID = 9900000;
        public const int BaseFolderID = 1000;

        public static RoutedCommand EventSwitchUpCommand = new RoutedCommand();
        public static RoutedCommand EventSwitchDownCommand = new RoutedCommand();

        private bool _processEventSwitch = true;
        public List<string> EventNames { get; set; }
        public GameGeneratorWindow()
        {
            InitializeComponent();

            ToolTipService.ShowDurationProperty.OverrideMetadata(
                typeof(DependencyObject), new FrameworkPropertyMetadata(int.MaxValue));

            GameParameter = new GameParameter();
            this.DataContext = new Event();
            EventNames = new List<string>();
            lstRaces.ItemsSource = EventNames;
            cb_QuickEventPicker.ItemsSource = EventNames;

            Settings = new LocalSettings();
        }

        ~GameGeneratorWindow()
        {
            if (Client != null && !Client.IsDisposed)
                Client.Dispose();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            Settings.Save(".settings");
            if (Client != null && !Client.IsDisposed)
                Client.Dispose();
        }

        private void GameGenerator_Load(object sender, EventArgs e)
        {
            var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "data", "data.db");
            if (!File.Exists(dbPath))
            {
                MessageBox.Show("Required database file for the generator is missing (data/data.db), exiting.", "Database file missing");
                this.Close();
                return;
            }

            GameDatabase = new GameDB(Path.Combine(Directory.GetCurrentDirectory(), "Data", "data.db"));
            if (!GameDatabase.CreateConnection())
            {
                MessageBox.Show("Could not connect to local database (data.db).");
                Environment.Exit(0);
            }

            // Load Settings
            if (File.Exists(".settings"))
                Settings.ReadFromFile(".settings");
            else
                Settings.CreateDefault();

            // Discord Presence 
            Client = new DiscordRpcClient("784198457220005899");
            if (Settings.HasEnabledSetting("Discord_Presence_Enabled"))
            {
                Client.Initialize();
                UpdateDiscordPresence();
                DiscordRichPresenceMenuItem.IsChecked = true;
            }
            minimizeXMLToolStripMenuItem.IsChecked = Settings.HasEnabledSetting("Minify_XML");

            txtGameParamName.Text = GameParameter.EventList.Title;
            txtGameParamDesc.Text = GameParameter.EventList.Description;
            tb_FolderFileName.Text = GameParameter.FolderFileName;

            tabEvent.SelectionChanged += new SelectionChangedEventHandler(tabEvent_Selecting);

            var categories = GameDatabase.GetFolderCategoriesSorted();
            foreach (var category in categories)
            {
                GameParameterEventList.EventCategories.Add(new EventCategory(category.CategoryName, category.CategoryType));
                cboEventCategory.Items.Add(category.CategoryName);
            }

            cboEventCategory.SelectedIndex = 0;

            var langs = GameDatabase.GetLocalizedLanguagesSorted();

            foreach (var lang in langs)
                GameParameterEventList.LocaliseLanguages.Add(lang);

            foreach (var i in (GameMode[])Enum.GetValues(typeof(GameMode)))
                cb_gameModes.Items.Add(i.Humanize());

            foreach (var i in (SpecType[])Enum.GetValues(typeof(SpecType)))
                cb_Spec.Items.Add(i.Humanize());

            foreach (var i in (PlayType[])Enum.GetValues(typeof(PlayType)))
                cb_PlayType.Items.Add(i.Humanize());

            cb_gameModes.SelectedIndex = 0;
            cb_Spec.SelectedIndex = 0;
            cb_PlayType.SelectedIndex = 0;

            EventSwitchUpCommand.InputGestures.Add(new KeyGesture(Key.Up, ModifierKeys.Control));
            EventSwitchDownCommand.InputGestures.Add(new KeyGesture(Key.Down, ModifierKeys.Control));
            CommandBindings.Add(new CommandBinding(EventSwitchUpCommand, EventSwitchUpCommand_Executed));
            CommandBindings.Add(new CommandBinding(EventSwitchDownCommand, EventSwitchDownCommand_Executed));

        }

        #region Menu Bar
        private void exportEventToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (GameParameter.Events == null || GameParameter.Events.Count == 0)
            {
                MessageBox.Show("Cannot generate a folder with no events. Please add at least one event to this folder and try again.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            for (int i = 0; i < GameParameter.Events.Count; i++)
            {
                Event evnt = GameParameter.Events[i];
                if (evnt.RaceParameters.RacersMax - (evnt.Entries.AI.Count + 1) < 0)
                {
                    MessageBox.Show($"Event #{i + 1} has invalid fixed ai amounts - it is generating more entries than 'Max Cars' allows and would crash the game.\n" +
                        $"Ensure that the total amount of Fixed entries + Player entries is inferior to the total of Max Cars in the event settings tab.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                else if (evnt.Entries.AI.Any())
                {
                    if (evnt.Entries.AIEntryGenerateType == EntryGenerateType.ENTRY_BASE_SHUFFLE || evnt.Entries.AIEntryGenerateType == EntryGenerateType.ENTRY_BASE_ORDER)
                    {
                        MessageBox.Show($"Event #{i + 1} has fixed entries and 'AI Pool Generate Type' to '{evnt.Entries.AIEntryGenerateType.Humanize()}' - that will crash or softlock the game" +
                       "as the game will try to pick from the AI pool while fixed entries exist. You cannot have both of each.\n" +
                       "- If you want only fixed entries, set 'AI Pool Generate Type' to 'None'.\n" +
                       "- If you want random entries, set it to Shuffle/Order, and remove your fixed entries.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    else if (evnt.Entries.Player is null)
                    {
                        MessageBox.Show($"Event #{i + 1} has fixed entries but the player does not have a rented car. The player must be using a rented a car.", 
                            "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                }
            }

            var saveFile = new System.Windows.Forms.FolderBrowserDialog();

            //saveFile.SelectedPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            saveFile.ShowDialog();
            if (string.IsNullOrEmpty(saveFile.SelectedPath))
                return;

            selectedPath = saveFile.SelectedPath;

            GenerateGameParameter();

        }

        private void importEventListToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult importOverwrite = MessageBox.Show("This will overwrite the folder you are currently editing. Would you like to save your folder now?",
                "Import Folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);

            if (importOverwrite == MessageBoxResult.Yes)
                btnEventGenerate_Click(sender, e);
            else if (importOverwrite == MessageBoxResult.Cancel)
                return;

            var openFile = new OpenFileDialog();
            openFile.InitialDirectory = Directory.GetCurrentDirectory();
            openFile.Filter = "Event List XML Files (r/l*.xml) (*.xml)|*.xml";
            openFile.Title = "Import Events";
            openFile.ShowDialog();

            if (openFile.FileName.Contains(".xml"))
            {
                GameParameter = ImportFromEventList(openFile.FileName);

                // Set names
                for (int i = 0; i < GameParameter.Events.Count; i++)
                {
                    var evnt = GameParameter.Events[i];
                    if (!string.IsNullOrEmpty(evnt.Information.Titles["GB"])) // Grab GB one if provided
                    {
                        GameParameter.Events[i].Name = evnt.Information.Titles["GB"];
                    }
                    else
                    {
                        GameParameter.Events[i].Name = $"Event {i + 1}";
                        GameParameter.Events[i].Information.SetTitle($"Event {i + 1}");
                    }
                }

                OnNewEventSelected(0);
                ReloadEventLists();
                UpdateEventListing();

                if (GameParameter.Events != null && GameParameter.Events.Count > 0)
                    ToggleEventControls(true);
                else
                    ToggleEventControls(false);

                RefreshFolderControls();
            }
        }

        private void newEventToolStripMenuItem_Click(object sender, EventArgs e)
        {
            MessageBoxResult newOverwrite = MessageBox.Show("This will delete the folder you are currently editing. Would you like to save your folder now?", "New Event", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);

            if (newOverwrite == MessageBoxResult.Yes)
                btnEventGenerate_Click(sender, e);
            else if (newOverwrite == MessageBoxResult.No)
            {
                GameParameter = new GameParameter();
                CurrentEvent = null;

                RefreshFolderControls();
                txtEventName.Text = "";

                tabEvent.SelectedIndex = 0;

                ReloadEventLists();
                UpdateEventListing();
            }
        }

        private void importEventToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult importOverwrite = MessageBox.Show("This will overwrite the folder you are currently editing. Would you like to save your folder now?",
                "Import Folder", MessageBoxButton.YesNoCancel, MessageBoxImage.Information);

            if (importOverwrite == MessageBoxResult.Yes)
                btnEventGenerate_Click(sender, e);
            else if (importOverwrite == MessageBoxResult.Cancel)
                return;

            var openFile = new OpenFileDialog();
            openFile.InitialDirectory = Directory.GetCurrentDirectory();
            openFile.Filter = "Folder XML Files (i.e sundaycup.xml) (*.xml)|*.xml";
            openFile.Title = "Import Folder";
            openFile.ShowDialog();

            if (openFile.FileName.Contains(".xml"))
            {
                try
                {
                    GameParameter = ImportFolder(openFile.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not import folder\nError: {ex.Message}",
                        "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                OnNewEventSelected(0);
                ReloadEventLists();
                UpdateEventListing();

                if (GameParameter.Events != null && GameParameter.Events.Count > 0)
                    ToggleEventControls(true);
                else
                    ToggleEventControls(false);

                RefreshFolderControls();
            }
        }

        private void exit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void minimizeXMLToolStripMenuItem_Checked(object sender, RoutedEventArgs e)
            => Settings.SetSettingValue("Minify_XML", minimizeXMLToolStripMenuItem.IsChecked);

        private void randomizeAINamesToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in CurrentEvent.Entries.AI)
            {
                var driverInfo = GameDatabase.GetRandomDriverInfo();
                var regionInfo = RegionUtil.GetRandomInitial(_random, driverInfo.InitialType);

                entry.DriverName = $"{regionInfo.initial}. {driverInfo.DriverName}";
                entry.DriverRegion = regionInfo.country;
            }

            foreach (var entry in CurrentEvent.Entries.AIBases)
            {
                var driverInfo = GameDatabase.GetRandomDriverInfo();
                var regionInfo = RegionUtil.GetRandomInitial(_random, driverInfo.InitialType);

                entry.DriverName = $"{regionInfo.initial}. {driverInfo.DriverName}";
                entry.DriverRegion = regionInfo.country;
            }

            CurrentEvent.Entries.NeedsPopulating = true;
            if ((tabEvent.SelectedItem as TabItem).Name.Equals("tabEntries"))
                PopulateEntries();
        }

        private void randomizeAIRoughnessMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (numericUpDown_AIRoughnessMin.Value > numericUpDown_AIRoughnessMax.Value)
                numericUpDown_AIRoughnessMax.Value = numericUpDown_AIRoughnessMax.Value;

            foreach (var entry in CurrentEvent.Entries.AI)
                entry.Roughness = _random.Next(numericUpDown_AIRoughnessMin.Value.Value, numericUpDown_AIRoughnessMax.Value.Value + 1);

            foreach (var entry in CurrentEvent.Entries.AIBases)
                entry.Roughness = _random.Next(numericUpDown_AIRoughnessMin.Value.Value, numericUpDown_AIRoughnessMax.Value.Value + 1);
        }

        private void randomizeAIBaseSkillMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (numericUpDown_BaseSkillMin.Value > numericUpDown_BaseSkillMax.Value)
                numericUpDown_BaseSkillMax.Value = numericUpDown_BaseSkillMin.Value;

            foreach (var entry in CurrentEvent.Entries.AI)
                entry.BaseSkill = _random.Next(numericUpDown_BaseSkillMin.Value.Value, numericUpDown_BaseSkillMax.Value.Value + 1);

            foreach (var entry in CurrentEvent.Entries.AIBases)
                entry.BaseSkill = _random.Next(numericUpDown_BaseSkillMin.Value.Value, numericUpDown_BaseSkillMax.Value.Value + 1);
        }

        private void randomizeAICornerSkillMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (numericUpDown_CornerSkillMin.Value > numericUpDown_CornerSkillMax.Value)
                numericUpDown_CornerSkillMax.Value = numericUpDown_CornerSkillMin.Value;

            foreach (var entry in CurrentEvent.Entries.AI)
                entry.CorneringSkill = _random.Next(numericUpDown_CornerSkillMin.Value.Value, numericUpDown_CornerSkillMax.Value.Value + 1);

            foreach (var entry in CurrentEvent.Entries.AIBases)
                entry.CorneringSkill = _random.Next(numericUpDown_CornerSkillMin.Value.Value, numericUpDown_CornerSkillMax.Value.Value + 1);
        }

        private void randomizeAIBrakingSkillMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (numericUpDown_BrakeSkillMin.Value > numericUpDown_BrakeSkillMax.Value)
                numericUpDown_BrakeSkillMax.Value = numericUpDown_BrakeSkillMin.Value;

            foreach (var entry in CurrentEvent.Entries.AI)
                entry.BrakingSkill = _random.Next(numericUpDown_BrakeSkillMin.Value.Value, numericUpDown_BrakeSkillMax.Value.Value + 1);

            foreach (var entry in CurrentEvent.Entries.AIBases)
                entry.BrakingSkill = _random.Next(numericUpDown_BrakeSkillMin.Value.Value, numericUpDown_BrakeSkillMax.Value.Value + 1);
        }

        private void randomizeAIAccelSkillMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (numericUpDown_AccelSkillMin.Value > numericUpDown_AccelSkillMax.Value)
                numericUpDown_AccelSkillMax.Value = numericUpDown_AccelSkillMin.Value;

            foreach (var entry in CurrentEvent.Entries.AI)
                entry.AccelSkill = _random.Next(numericUpDown_AccelSkillMin.Value.Value, numericUpDown_AccelSkillMax.Value.Value + 1);

            foreach (var entry in CurrentEvent.Entries.AIBases)
                entry.AccelSkill = _random.Next(numericUpDown_AccelSkillMin.Value.Value, numericUpDown_AccelSkillMax.Value.Value + 1);
        }

        private void randomizeAIStartSkillMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (numericUpDown_StartSkillMin.Value > numericUpDown_StartSkillMax.Value)
                numericUpDown_StartSkillMax.Value = numericUpDown_StartSkillMin.Value;

            foreach (var entry in CurrentEvent.Entries.AI)
                entry.StartingSkill = _random.Next(numericUpDown_StartSkillMin.Value.Value, numericUpDown_StartSkillMax.Value.Value + 1);

            foreach (var entry in CurrentEvent.Entries.AIBases)
                entry.StartingSkill = _random.Next(numericUpDown_StartSkillMin.Value.Value, numericUpDown_StartSkillMax.Value.Value + 1);
        }

        private void randomizeAISkillsMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("This will regenerate ALL AI skills for the current event. Continue?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Information)
                == MessageBoxResult.Yes)
            {
                randomizeAIRoughnessMenuItem_Click(sender, e);
                randomizeAIBaseSkillMenuItem_Click(sender, e);
                randomizeAICornerSkillMenuItem_Click(sender, e);
                randomizeAIBrakingSkillMenuItem_Click(sender, e);
                randomizeAIAccelSkillMenuItem_Click(sender, e);
                randomizeAIStartSkillMenuItem_Click(sender, e);
            }
        }

        private void randomizeAITireCompoundToolStripMenuItem_Click(object sender, RoutedEventArgs e)
        {
            foreach (var entry in CurrentEvent.Entries.AI)
            {
                entry.TireFront = (TireType)comboBox_AIGenTyreComp.SelectedIndex - 1;
                entry.TireRear = (TireType)comboBox_AIGenTyreComp.SelectedIndex - 1;
            }

            foreach (var entry in CurrentEvent.Entries.AIBases)
            {
                entry.TireFront = (TireType)comboBox_AIGenTyreComp.SelectedIndex - 1;
                entry.TireRear = (TireType)comboBox_AIGenTyreComp.SelectedIndex - 1;
            }
        }

        private void cb_QuickEventPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (cb_QuickEventPicker.SelectedIndex != -1 && _processEventSwitch)
                OnNewEventSelected(cb_QuickEventPicker.SelectedIndex);
        }

        #endregion

        #region Event Select
        private void tabEvent_Selecting(object sender, SelectionChangedEventArgs e)
        {
            if (e.Source is TabControl tabControl)
            {
                if (tabControl.SelectedIndex > 2 && CurrentEvent is null)
                {
                    MessageBox.Show("Create an Event to edit any event first.", "Event missing");
                    e.Handled = false;
                    return;
                }


                if (tabControl.SelectedIndex == 1)
                    UpdateEventListing();
                else
                    PopulateSelectedTab();

                UpdateDiscordPresence();
            }
        }

        private void lstEvent_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstRaces.SelectedIndex != -1 && _processEventSwitch)
                OnNewEventSelected(lstRaces.SelectedIndex);
        }

        public void EventSwitchUpCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!GameParameter.Events.Any())
                return;

            if (lstRaces.SelectedIndex > 0)
                OnNewEventSelected(lstRaces.SelectedIndex - 1);
        }

        public void EventSwitchDownCommand_Executed(object sender, ExecutedRoutedEventArgs e)
        {
            if (!GameParameter.Events.Any())
                return;

            if (lstRaces.SelectedIndex < lstRaces.Items.Count - 1)
                OnNewEventSelected(lstRaces.SelectedIndex + 1);
        }

        #endregion

        private void btnAddEvent_Click(object sender, EventArgs e)
        {
            Event evnt = new Event();
            evnt.Index = GameParameter.Events.Count + 1;
            evnt.Name = $"Event {evnt.Index}";
            evnt.Rewards.Stars = 3;

            EventNames.Add($"{evnt.Index} - {evnt.Name}");
            GameParameter.Events.Add(evnt);
            GameParameter.OrderEventIDs();

            this.DataContext = evnt;

            _processEventSwitch = false;
            UpdateEventListing();
            SelectEvent(evnt.Index - 1);
            rdoStarsThree.IsChecked = true;

            ToggleEventControls(true);

            btnRemoveRace.IsEnabled = GameParameter.Events.Count <= 100;
            btnCopyRace.IsEnabled = GameParameter.Events.Count <= 100;
            _processEventSwitch = true;
        }

        private void txtEventName_TextChanged(object sender, EventArgs e)
        {
            if (CurrentEvent is null || !_processEventSwitch)
                return;

            CurrentEvent.Name = (sender as TextBox).Text;
            _processEventSwitch = false;
            txtEventName.Text = CurrentEvent.Name;
            txt_EventTitle.Text = CurrentEvent.Name;
            _processEventSwitch = true;

            CurrentEvent.Information.SetTitle(CurrentEvent.Name);

            int currentEventIndex = GameParameter.Events.IndexOf(CurrentEvent);

            if (currentEventIndex < EventNames.Count)
                EventNames[currentEventIndex] = $"{CurrentEvent.Index} - {CurrentEvent.Name}";

            ReloadEventLists();
            UpdateEventListing();

            _processEventSwitch = false;
            cb_QuickEventPicker.SelectedIndex = currentEventIndex;
            _processEventSwitch = true;
        }

        private void btnRemoveRace_Click(object sender, EventArgs e)
        {
            if (lstRaces.SelectedIndex == -1)
            { 
                MessageBox.Show("No event selected.", "", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult deletionResult = MessageBox.Show($"Are you sure you wish to delete the event \"{CurrentEvent.Name}\"?", "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (deletionResult == MessageBoxResult.Yes)
            {
                GameParameter.Events.Remove(GameParameter.Events.Find(x => x.Index == CurrentEvent.Index));
                CurrentEvent = GameParameter.Events.Count > 0 ? GameParameter.Events.Last() : null;

                int eventID = GameParameter.Events.FirstOrDefault()?.EventID ?? GameParameter.BaseEventID;
                GameParameter.FirstEventID = eventID;
                GameParameter.OrderEventIDs();

                ReloadEventLists(GameParameter.Events.Count - 1);
                UpdateEventListing();

                if (lstRaces.Items.Count == 0)
                {
                    btnRemoveRace.IsEnabled = false;
                    btnCopyRace.IsEnabled = false;
                    ToggleEventControls(false);
                }
                else
                    PopulateEventDetails();
            }
        }

        private void btnCopyRace_Click(object sender, EventArgs e)
        {
            if (lstRaces.SelectedIndex == -1)
            {
                MessageBox.Show("No event selected.", "", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var copy = DeepCloner.Clone(CurrentEvent);
            copy.Index = GameParameter.Events.Count + 1;
            for (int i = 0; i < CurrentEvent.Rewards.MoneyPrizes.Length; i++)
                copy.Rewards.MoneyPrizes[i] = CurrentEvent.Rewards.MoneyPrizes[i];

            for (int i = 0; i < CurrentEvent.Rewards.MoneyPrizes.Length; i++)
                copy.Rewards.PointTable[i] = CurrentEvent.Rewards.PointTable[i];
            copy.RaceParameters.Date = CurrentEvent.RaceParameters.Date;
            EventNames.Add($"{copy.Index} - {copy.Name}");
            GameParameter.Events.Add(copy);
            GameParameter.OrderEventIDs();

            _processEventSwitch = false;
            SelectEvent(copy.Index - 1);
            this.DataContext = copy;
            UpdateEventListing();
            rdoStarsThree.IsChecked = true;

            ToggleEventControls(true);

            btnRemoveRace.IsEnabled = GameParameter.Events.Count <= 100;
            btnCopyRace.IsEnabled = GameParameter.Events.Count <= 100;
            _processEventSwitch = true;
        }

        private void btnOpenMenuDB_Click(object sender, EventArgs e)
        {
            var openFile = new OpenFileDialog();

            openFile.InitialDirectory = Directory.GetCurrentDirectory();
            openFile.Filter = "menudb.dat|menudb.dat";
            openFile.Title = "Open menudb.dat";
            openFile.ShowDialog();

            if (openFile.FileName.Contains("menudb.dat"))
                CheckMenuDB(openFile.FileName);
        }

        private void btnEventGenerate_Click(object sender, EventArgs e)
        {
            if (GameParameter.Events == null || GameParameter.Events.Count == 0)
                MessageBox.Show("Cannot generate a folder with no event. Please add at least one race to this event and try again.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
            else
                GenerateGameParameter();
        }

        private void cboEventCategory_SelectedIndexChanged(object sender, RoutedEventArgs e)
        {
            if (cboEventCategory.SelectedItem is null)
                cboEventCategory.SelectedIndex = 0;
            GameParameter.EventList.Category = GameParameterEventList.EventCategories.Find(x => x.name == cboEventCategory.SelectedItem.ToString());
        }

        private void txtFolderName_TextChanged(object sender, EventArgs e)
        {
            GameParameter.EventList.Title = txtGameParamName.Text;
        }

        private void txtFolderDesc_TextChanged(object sender, EventArgs e)
        {
            GameParameter.EventList.Description = txtGameParamDesc.Text;
        }

        private void iud_StarsNeeded_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (GameParameter is null)
                return;

            var iud = ((Xceed.Wpf.Toolkit.IntegerUpDown)sender);
            if (iud.Value is null)
                iud.Value = (int)e.OldValue;

            GameParameter.EventList.StarsNeeded = iud.Value.Value;
        }

        private void iud_FolderID_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (GameParameter is null)
                return;

            var iud = ((Xceed.Wpf.Toolkit.IntegerUpDown)sender);
            if (iud.Value is null)
                iud.Value = (int)e.OldValue;

            GameParameter.FolderId = iud.Value.Value;
        }

        private void iud_EventID_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (iud_EventID.Value is null || GameParameter.Events.Count == 0)
                return;

            GameParameter.FirstEventID = GameParameter.Events[0].EventID;
            GameParameter.OrderEventIDs();
        }

        private void rdoStarsNone_CheckedChanged(object sender, EventArgs e)
            => CurrentEvent.Rewards.Stars = 0;

        private void rdoStarsOne_CheckedChanged(object sender, EventArgs e)
            => CurrentEvent.Rewards.Stars = rdoStarsOne.IsChecked.Value ? 1 : 0;

        private void rdoStarsThree_CheckedChanged(object sender, EventArgs e)
            => CurrentEvent.Rewards.Stars = rdoStarsThree.IsChecked.Value ? 3 : 0;

        private void cb_gameModes_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurrentEvent is null)
                return;

            CurrentEvent.GameMode = (GameMode)(sender as ComboBox).SelectedIndex;
        }

        private void cb_Spec_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurrentEvent is null)
                return;

            CurrentEvent.PlayStyle.SpecType = (SpecType)(sender as ComboBox).SelectedIndex;
        }

        private void cb_PlayType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CurrentEvent is null)
                return;

            CurrentEvent.PlayStyle.PlayType = (PlayType)(sender as ComboBox).SelectedIndex;
        }

        private void chkIsChampionship_CheckedChanged(object sender, EventArgs e)
        {
            btnChampionshipRewards.IsEnabled = chkIsChampionship.IsChecked.Value;
            GameParameter.EventList.IsChampionship = chkIsChampionship.IsChecked.Value;
        }

        public void btnChampionshipRewards_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CreditXPEditWindow(GameParameter.SeriesRewardCredits);
            dlg.ShowDialog();

            if (dlg.Saved)
                GameParameter.SeriesRewardCredits = dlg.Values;
        }

        private void btnPickImage_Click(object sender, EventArgs e)
        {
            var openImage = new OpenFileDialog();

            openImage.InitialDirectory = Directory.GetCurrentDirectory();
            openImage.Filter = "All files|*.*|BMP Images|*.bmp|JPEG Images|*.jpg|PNG Images|*.png";
            openImage.Title = "Open Image";
            openImage.ShowDialog();

            if (!openImage.FileName.ToLower().Contains(".bmp") && !openImage.FileName.ToLower().Contains(".jpg") && !openImage.FileName.ToLower().Contains(".png"))
            {
                MessageBox.Show("Input file was not a supported image format. Please input a BMP, JPG, or PNG image and try again.", "Open Image", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            
            _eventImage = System.Drawing.Image.FromFile(openImage.FileName);

            using (Graphics graphic = Graphics.FromImage(new Bitmap(_eventImage)))
            {
                _eventImage = MiscUtils.ResizeImage(_eventImage, new System.Drawing.Size(432, 244));
                pctImagePreview.Source = new BitmapImage(new Uri(openImage.FileName));
            }
        }

        private void btnSaveImage_Click(object sender, EventArgs e)
        {
            string path = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            if (!File.Exists(Path.Combine(path, "texconv.exe")))
            {
                MessageBox.Show("TexConv not found. Please download TexConv from https://github.com/microsoft/DirectXTex/releases and place it in the program folder.",
                    "Save Image", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            else if (!File.Exists(Path.Combine(path, "TXS3Converter.exe")))
            {
                MessageBox.Show("TexConv not found. Please download TexConv from https://github.com/microsoft/DirectXTex/releases and place it in the program folder.",
                    "Save Image", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (_eventImage is null)
            {
                MessageBox.Show("No image selected, pick one first.", "No image chosen", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string imageFileName = $"{GameParameter.FolderFileName}_{CurrentEvent.Index.ToString("00")}";
            string imageFilePath = Path.Combine(Directory.GetCurrentDirectory(), imageFileName + ".png");

            _eventImage.Save(imageFilePath, ImageFormat.Png);

            Process p = new Process();
            p.StartInfo.FileName = Path.Combine(Directory.GetCurrentDirectory(), "TXS3Converter.exe");
            p.StartInfo.Arguments = $"{Path.GetFileName(imageFilePath)} --DXT3";
            p.StartInfo.CreateNoWindow = true;
            p.Start();
            p.WaitForExit();

            string newPath = Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), "output", "piece", "gt6", "event_flyer")).FullName;

            string imgOutput = Path.Combine(Directory.GetCurrentDirectory(), $"{imageFileName}.img");
            string finalPath = Path.Combine(newPath, imageFileName + ".img");

            try
            {
                File.Move(imgOutput, finalPath);
            } 
            catch (Exception ex)
            {
                MessageBox.Show("Could not convert file.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            File.Delete(Path.Combine(Directory.GetCurrentDirectory(), imageFileName + ".png"));

            MessageBox.Show($"Imaged saved as: \n'{finalPath}'.\n\n When packing, move the entire \"piece\" folder to your mod folder.", "Image saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void cb_StartTime_Checked(object sender, RoutedEventArgs e)
        {
            date_Date.IsEnabled = cb_StartTime.IsChecked == true;
            if (!date_Date.IsEnabled)
                CurrentEvent.RaceParameters.Date = null;
        }

        private void tb_FolderFileName_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrEmpty(tb_FolderFileName.Text))
            {
                GameParameter.FolderFileName = Regex.Replace(GameParameter.EventList.Title.Replace(" ", "").Replace(".", ""), "[^a-zA-Z0-9_.]+", "", RegexOptions.Compiled).ToLower();
                tb_FolderFileName.Text = GameParameter.FolderFileName;
            }
            else
                GameParameter.FolderFileName = tb_FolderFileName.Text;
        }

        private void iud_RacerMax_ValueChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {

        }

        #region Non Generated
        // ------ Non-generated functions ------

        public void UpdateEventListing()
        {
            // Why does refresh unselect my items??? Ugly workaround
            int index = lstRaces.SelectedIndex;
            lstRaces.Items.Refresh();
            cb_QuickEventPicker.Items.Refresh();
            _processEventSwitch = false;
            lstRaces.SelectedIndex = index;
            cb_QuickEventPicker.SelectedIndex = index;
            _processEventSwitch = true;
        }

        /// <summary>
        /// When a new event is selected.
        /// </summary>
        /// <param name="eventIndex"></param>
        public void OnNewEventSelected(int eventIndex)
        {
            _processEventSwitch = false;
            SelectEvent(eventIndex);

            this.DataContext = CurrentEvent;

            CurrentEvent.MarkUnpopulated();
            PopulateSelectedTab();
            PopulateEventDetails();
            _processEventSwitch = true;
            UpdateDiscordPresence();
        }

        /// <summary>
        /// Swaps to the specified event index and updates the event list & quick switcher.
        /// </summary>
        /// <param name="index"></param>
        public void SelectEvent(int index)
        {
            if (index != -1)
                CurrentEvent = GameParameter.Events[index];

            cb_QuickEventPicker.SelectedIndex = index;
            lstRaces.SelectedIndex = index;
        }

        private void PopulateEventDetails()
        {
            cb_gameModes.SelectedIndex = (int)CurrentEvent.GameMode;
            cb_Spec.SelectedIndex = (int)CurrentEvent.PlayStyle.SpecType;
            cb_PlayType.SelectedIndex = (int)CurrentEvent.PlayStyle.PlayType;

            if (CurrentEvent.Rewards.Stars == 0)
                rdoStarsNone.IsChecked = true;
            else if (CurrentEvent.Rewards.Stars == 1)
                rdoStarsOne.IsChecked = true;
            else if (CurrentEvent.Rewards.Stars == 3)
                rdoStarsThree.IsChecked = true;
            txtEventName.Text = CurrentEvent.Name;

            iud_EventID.IsEnabled = GameParameter.Events.Any() && CurrentEvent == GameParameter.Events[0];
        }

        private void ReloadEventLists(int selectedIndex = 0, bool isQuickPick = false)
        {
            _processEventSwitch = false;
            EventNames.Clear();
            // Rebuild list from 1 e.g. if 1, 2, 3, delete 2, 3 becomes 2
            int eventCounter = 1;
            for (int i = 0; i < GameParameter.Events.Count; i++)
            {
                Event evnt = GameParameter.Events[i];
                evnt.Index = eventCounter++;
                EventNames.Add($"{evnt.Index} - {evnt.Name}");
            }


            if (GameParameter.Events.Count == 0)
            {
                lstRaces.SelectedIndex = -1;
                cb_QuickEventPicker.SelectedIndex = -1;
                ToggleEventControls(false);
            }
            else
            {
                lstRaces.SelectedIndex = selectedIndex;
                cb_QuickEventPicker.SelectedIndex = selectedIndex;
            }
            _processEventSwitch = true;
        }

        void ToggleEventControls(bool isEnabled)
        {
            for (int i1 = 2; i1 < tabEvent.Items.Count; i1++)
            {
                var i = (TabItem)tabEvent.Items[i1];
                i.IsEnabled = isEnabled;
            }

            txtEventName.IsEnabled = isEnabled;
            btnCreditRewards.IsEnabled = isEnabled;
            rdoStarsNone.IsEnabled = isEnabled;
            rdoStarsOne.IsEnabled = isEnabled;
            rdoStarsThree.IsEnabled = isEnabled;
            cb_gameModes.IsEnabled = isEnabled;
            cb_Spec.IsEnabled = isEnabled;
            cb_PlayType.IsEnabled = isEnabled;
            iud_RacerMax.IsEnabled = isEnabled;
            checkBox_SeasonalEvent.IsEnabled = isEnabled;

            checkBox_SeasonalEvent.IsEnabled = isEnabled;
            cb_QuickEventPicker.IsEnabled = isEnabled;

            iud_EventID.IsEnabled = isEnabled && GameParameter.Events.Any() && CurrentEvent == GameParameter.Events[0];

            btnAddRace.IsEnabled = GameParameter?.Events.Count < 100;
            btnRemoveRace.IsEnabled = GameParameter?.Events?.Any() == true;
            btnCopyRace.IsEnabled = GameParameter?.Events?.Any() == true;

            randomizeAINamesToolStripMenuItem.IsEnabled = isEnabled;

            randomizeAIRoughnessToolStripMenuItem.IsEnabled = isEnabled;
            randomizeAIBaseSkillToolStripMenuItem.IsEnabled = isEnabled;
            randomizeAICornerSkillToolStripMenuItem.IsEnabled = isEnabled;
            randomizeAIBrakingSkillToolStripMenuItem.IsEnabled = isEnabled;
            randomizeAIAccelSkillToolStripMenuItem.IsEnabled = isEnabled;
            randomizeAIStartSkillToolStripMenuItem.IsEnabled = isEnabled;

            randomizeAISkillsToolStripMenuItem.IsEnabled = isEnabled;
            randomizeAITireCompoundToolStripMenuItem.IsEnabled = isEnabled;
        }

        void CheckMenuDB(string file)
        {
            MenuDB = new MenuDB(file);

            try
            {
                if (MenuDB.CreateConnection())
                {
                    // Check if the table we need exists
                    if (MenuDB.GetFolderNameByID(23).Equals("sundaycup"))
                        MessageBox.Show("Menudb.dat valid!", "Menudb.dat", MessageBoxButton.OK, MessageBoxImage.Information);
                    else
                        MessageBox.Show("Could not verify menudb.dat, please try again.", "Menudb.dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("No MenuDB connection was established, please try again.", "Menudb.dat", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception e)
            {
                MessageBox.Show($"Could not open MenuDB.dat: {e.Message}", "Menudb.dat error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        void GenerateGameParameter()
        {
            if (/*!eventHasNoStars && */!validationErrors)
            {
                if (selectedPath == "")
                {
                    if (menuDBValid)
                    {
                        SerializeGameParameter(true);
                    }
                    else
                    {
                        MessageBox.Show("Menudb.dat could not be verified. This event cannot be automatically generated. " +
                            "Please either provide a valid menudb.dat or export your file to XML from the File menu.", "Event Generation Information", MessageBoxButton.OK,
                                        MessageBoxImage.Information);
                    }
                }
                else
                {
                    SerializeGameParameter(false);
                }
            }
            else
            {
                /*
                if (eventHasNoStars)
                    MessageBox.Show("One or more races has no stars assigned to it. Please pick a number of stars and try again.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    */
                if (validationErrors)
                {
                    MessageBox.Show("One or more text fields is blank. Please populate the highlighted fields and try again.", "Validation Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    validationErrors = false;
                }
            }
        }

        public void SerializeGameParameter(bool shouldEditDB)
        {
            /*
            if (menuDBValid && shouldEditDB)
            {
                

                int lastID = MenuDB.GetLastFolderID();
               
                    // If we can read the menudb then we can increment based on what's already in it to avoid a clash
                    firstSafeFolderID = lastID + 1;

                    // If not already input - probably from saved file
                    if (string.IsNullOrEmpty(GameParameter.EventList.FolderID))
                        GameParameter.EventList.FolderID = firstSafeFolderID.ToString();

                    if (GameParameter.FolderId == -1)
                        GameParameter.FolderId = firstSafeFolderID;
                

                firstSafeTitleID = MenuDB.GetLastFolderLocalizeID() + 1;

                results = MenuDB.ExecuteQuery(string.Format("SELECT COALESCE(MAX(FolderOrder), 0) FROM t_event_folder WHERE Type = {0}", GameParameter.EventList.Category.typeID));
                while (results.Read())
                    firstSafeSortOrderInCategory = results.GetInt32(0) + 1;

                MenuDB.AddNewFolderID(GameParameter, firstSafeTitleID, firstSafeFolderID, firstSafeSortOrderInCategory);

                // To give it a shorter name and escape any 's
                string s = GameParameter.EventList.Title.Replace("'", "''");

                MenuDB.AddNewFolderLocalization(firstSafeTitleID, s);
                MenuDB.CloseConnection();
            }
            else
            {
                GameParameter.FolderId = BaseFolderID.ToString();
            }
            */

            if (selectedPath == "")
                selectedPath = Path.Combine(Directory.GetCurrentDirectory(), "output", "game_parameter", "gt6", "event");

            string mainParamFile = Path.Combine(selectedPath);

            bool minify = Settings.HasEnabledSetting("Minify_XML");
            GameParameter.EventList.WriteToXML(GameParameter, mainParamFile, BaseEventID, cboEventCategory.SelectedItem.ToString(), minify);

            using (var xml = XmlWriter.Create(Path.Combine(selectedPath, $"r{GameParameter.FolderId}.xml"), new XmlWriterSettings() { Indent = !minify, IndentChars = "  " }))
            {
                xml.WriteStartElement("xml");
                GameParameter.WriteToXml(xml);
                xml.WriteEndElement();
            }

            MessageBox.Show($"Event and races successfully written to {selectedPath}\\{GameParameter.FolderFileName}.xml and {selectedPath}\\r{GameParameter.FolderId}.xml!", 
                "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public GameParameter ImportFolder(string filePath)
        {
            var gp = new GameParameter();

            var doc = new XmlDocument();
            doc.Load(filePath);

            gp.EventList.ParseEventList(gp, doc);
            gp.EventList.Category = GameParameterEventList.EventCategories.FirstOrDefault(e => e.typeID == gp.EventList.Category.typeID);

            string dir = Path.GetDirectoryName(filePath);

            string eventListFile = Path.Combine(dir, $"r{gp.FolderId:000}.xml");
            if (!File.Exists(eventListFile))
                throw new FileNotFoundException($"Could not find file {eventListFile} referenced by the provided folder.");

            var settings = new XmlReaderSettings();
            settings.IgnoreComments = true;
            var reader = XmlReader.Create(eventListFile, settings);
            var eventDoc = new XmlDocument();
            eventDoc.Load(reader);

            gp.ParseEventsFromFile(eventDoc);

            gp.FolderFileName = Path.GetFileNameWithoutExtension(filePath);
            return gp;
        }

        public GameParameter ImportFromEventList(string filePath)
        {
            XmlDocument eventDoc = new XmlDocument();
            var settings = new XmlReaderSettings();
            settings.IgnoreComments = true;
            var reader = XmlReader.Create(filePath, settings);
            var gp = new GameParameter();
            eventDoc.Load(reader);

            gp.ParseEventsFromFile(eventDoc);
            return gp;
        }

        public void RefreshFolderControls()
        {
            txtGameParamName.Text = GameParameter.EventList.Title;
            txtGameParamDesc.Text = GameParameter.EventList.Description;
            iud_StarsNeeded.Value = GameParameter.EventList.StarsNeeded;
            iud_FolderID.Value = GameParameter.FolderId;
            tb_FolderFileName.Text = GameParameter.FolderFileName;
            chkIsChampionship.IsChecked = GameParameter.EventList.IsChampionship;
            btnChampionshipRewards.IsEnabled = GameParameter.EventList.IsChampionship;
            cboEventCategory.SelectedIndex = GameParameterEventList.EventCategories.IndexOf(GameParameter.EventList.Category);

            if (GameParameter.Events != null)
                ReloadEventLists(isQuickPick: false);
            else
                ToggleEventControls(false);
        }

        private void MenuItem_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Credits: " +
                "- Nenkai#9075 - Creator\n" +
                "- TheAdmiester - Co-Creator/Made the original tool\n" +
                "- Everyone who tested this tool and uses it - thank you!", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public void PopulateSelectedTab()
        {
            var current = tabEvent.SelectedItem as TabItem;
            if (current.Name.Equals("tabEventInfo"))
            {
                if (CurrentEvent.Information.NeedsPopulating)
                    PopulateEventInfoTab();
            }
            else if (current.Name.Equals("tabEventRegulation"))
            {
                if (CurrentEvent.Regulations.NeedsPopulating)
                    PrePopulateRegulations();
            }
            else if (current.Name.Equals("tabEventConstraints"))
            {
                if (CurrentEvent.Constraints.NeedsPopulating)
                    PopulateConstraints();
            }
            else if (current.Name.Equals("tabEventParams"))
            {
                if (CurrentEvent.RaceParameters.NeedsPopulating)
                    PopulateParameters();
            }
            else if (current.Name.Equals("tabEntries"))
            {
                PopulateEntries();
            }
            else if (current.Name.Equals("tabEventCourse"))
            {
                if (CurrentEvent.Course.NeedsPopulating)
                    PrePopulateCourses();
            }
            else if (current.Name.Equals("tabEvalConditions"))
            {
                if (CurrentEvent.EvalConditions.NeedsPopulating)
                    PrePopulateEvalConditions();
            }
            else if (current.Name.Equals("tabEventRewards"))
            {
                if (CurrentEvent.Rewards.NeedsPopulating)
                    PopulateRewards();
            }
        }

        #endregion


        public void UpdateDiscordPresence()
        {
            if (Settings.HasEnabledSetting("Discord_Presence_Enabled") && Client?.IsInitialized == true)
            {
                var presence = new RichPresence();
                if (CurrentEvent is null)
                {
                    presence.Details = "No Folder.";
                }
                else
                {
                    string name = CurrentEvent.Name.Length > 100 ? CurrentEvent.Name.Substring(0, 100) + "..." : CurrentEvent.Name;
                    presence.Details = $"{GameParameter.EventList.Title}";
                    presence.State = $"Event: {name}";
                    presence.Timestamps = Timestamps.Now;
                }

                presence.Assets = new Assets()
                {
                    LargeImageText = "Gran Turismo 5/6 Event Generator",
                    LargeImageKey = "icon",
                };

                Client.SetPresence(presence);
            }
        }

        private void decryptTedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var openFile = new OpenFileDialog();
            openFile.Filter = "Course Maker File (*.ted)|*.ted";
            openFile.Title = "Import Course Maker File to decrypt";
            if (openFile.ShowDialog() == true)
            {
                if (!CourseMakerUtil.Decrypt(openFile.FileName))
                {
                    MessageBox.Show("Could not decrypt TED file - File is already decrypted or not a valid TED Custom track.", "Error", 
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show("File successfully decrypted.", "Completed",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        private void encryptTedMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var openFile = new OpenFileDialog();
            openFile.Filter = "Course Maker File (*.ted)|*.ted";
            openFile.Title = "Import Course Maker File to encrypt";
            if (openFile.ShowDialog() == true)
            {
                if (!CourseMakerUtil.Encrypt(openFile.FileName))
                {
                    MessageBox.Show("Could not encrypt TED file - File is already encrypted or not a valid TED Custom track.", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show("File successfully encrypted.", "Completed",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }

        public void DiscordRichPresenceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            Settings.SetSettingValue("Discord_Presence_Enabled", DiscordRichPresenceMenuItem.IsChecked);
            if (DiscordRichPresenceMenuItem.IsChecked)
            {
                if (!Client.IsInitialized)
                    Client.Initialize();
                UpdateDiscordPresence();
            }
            else
            {
                if (Client.IsInitialized)
                    Client.Deinitialize();
            }
        }
    }
}
