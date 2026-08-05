namespace SeapowerMultiplayer.UI
{
    /// <summary>
    /// The overlay's XAML, kept as markup and parsed at runtime.
    ///
    /// Shipping it as a string rather than a file keeps the workshop item at the
    /// two files _info.ini and the merged DLL - a missing or stale .xaml in a
    /// workshop folder would otherwise leave the mod with no UI at all.
    ///
    /// The root Grid deliberately has no Background: in Noesis a null background
    /// is not hit-testable, so clicks anywhere outside the panel fall straight
    /// through to the game. That is what keeps the map usable with the overlay
    /// open. ("Transparent" would be the opposite - it hit-tests.)
    ///
    /// Control styles are declared locally rather than inherited from the game's
    /// application resources: those are keyed by TargetType and would silently
    /// restyle the whole overlay on a game UI update.
    /// </summary>
    internal static class OverlayXaml
    {
        internal const string Markup = @"
<Grid xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
      xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
      Background=""{x:Null}"">

  <Grid.Resources>
    <!-- Alpha on the panel backgrounds is the translucency: the map has to stay
         readable underneath. Carried over from the procedural textures the
         IMGUI overlay generated. -->
    <SolidColorBrush x:Key=""Bg.Panel""    Color=""#B30E1421""/>
    <SolidColorBrush x:Key=""Bg.Section""  Color=""#8C182333""/>
    <SolidColorBrush x:Key=""Bg.Alert""    Color=""#C71A1010""/>
    <SolidColorBrush x:Key=""Bg.Button""   Color=""#9E213350""/>
    <SolidColorBrush x:Key=""Bg.Hover""    Color=""#BF30466E""/>
    <SolidColorBrush x:Key=""Bg.Press""    Color=""#D9405F8F""/>
    <SolidColorBrush x:Key=""Bg.Field""    Color=""#B3101826""/>
    <SolidColorBrush x:Key=""Edge""        Color=""#4D3A5A80""/>
    <SolidColorBrush x:Key=""Text.Normal"" Color=""#FFD3DEEE""/>
    <SolidColorBrush x:Key=""Text.Dim""    Color=""#FF8095B4""/>
    <SolidColorBrush x:Key=""Text.Head""   Color=""#FFAFC8E8""/>
    <SolidColorBrush x:Key=""Text.Warn""   Color=""#FFFFB233""/>
    <SolidColorBrush x:Key=""Text.Off""    Color=""#FF5A6B80""/>

    <Style TargetType=""TextBlock"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Normal}""/>
      <Setter Property=""FontSize"" Value=""12""/>
      <Setter Property=""TextWrapping"" Value=""Wrap""/>
    </Style>

    <Style x:Key=""Dim"" TargetType=""TextBlock"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Dim}""/>
      <Setter Property=""FontSize"" Value=""11""/>
      <Setter Property=""TextWrapping"" Value=""Wrap""/>
    </Style>

    <Style x:Key=""Warn"" TargetType=""TextBlock"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Warn}""/>
      <Setter Property=""FontSize"" Value=""12""/>
      <Setter Property=""TextWrapping"" Value=""Wrap""/>
    </Style>

    <!-- Focusable=False on every control below except the text fields.
         A focused control answers the keyboard, and the overlay shares a
         keyboard with the game: a button left holding focus turned the Enter
         the player pressed for the sim into a button press on the panel.
         Nothing here needs keyboard operation - the panel is mouse-driven, and
         the text fields are the only place a key should land. -->

    <!-- Ordinary panel button -->
    <Style x:Key=""Btn"" TargetType=""Button"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Normal}""/>
      <Setter Property=""FontSize"" Value=""12""/>
      <Setter Property=""Margin"" Value=""0,2,0,0""/>
      <Setter Property=""Focusable"" Value=""False""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""Button"">
            <Border x:Name=""B"" CornerRadius=""4"" Padding=""8,4""
                    Background=""{StaticResource Bg.Button}""
                    BorderBrush=""{StaticResource Edge}"" BorderThickness=""1"">
              <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""B"" Property=""Background"" Value=""{StaticResource Bg.Hover}""/>
              </Trigger>
              <Trigger Property=""IsPressed"" Value=""True"">
                <Setter TargetName=""B"" Property=""Background"" Value=""{StaticResource Bg.Press}""/>
              </Trigger>
              <Trigger Property=""IsEnabled"" Value=""False"">
                <Setter Property=""Foreground"" Value=""{StaticResource Text.Off}""/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Foldout / section header: a full-width button that reads as a heading -->
    <Style x:Key=""Fold"" TargetType=""Button"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Head}""/>
      <Setter Property=""FontSize"" Value=""11""/>
      <Setter Property=""FontWeight"" Value=""Bold""/>
      <Setter Property=""Focusable"" Value=""False""/>
      <Setter Property=""HorizontalContentAlignment"" Value=""Stretch""/>
      <Setter Property=""Margin"" Value=""0,6,0,0""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""Button"">
            <Border x:Name=""B"" CornerRadius=""3"" Padding=""6,3""
                    Background=""{StaticResource Bg.Section}"">
              <ContentPresenter/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""B"" Property=""Background"" Value=""{StaticResource Bg.Hover}""/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Every control below carries an explicit Template. Declaring a Style for
         a templated control replaces the one it would otherwise inherit, so a
         Style that sets only colours leaves it with no template at all - which
         Noesis renders as a magenta placeholder. -->
    <Style x:Key=""Field"" TargetType=""TextBox"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Normal}""/>
      <Setter Property=""CaretBrush"" Value=""{StaticResource Text.Normal}""/>
      <Setter Property=""Background"" Value=""{StaticResource Bg.Field}""/>
      <Setter Property=""BorderBrush"" Value=""{StaticResource Edge}""/>
      <Setter Property=""BorderThickness"" Value=""1""/>
      <Setter Property=""Padding"" Value=""4,2""/>
      <Setter Property=""FontSize"" Value=""11""/>
      <Setter Property=""Width"" Value=""64""/>
      <Setter Property=""HorizontalAlignment"" Value=""Left""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""TextBox"">
            <Border CornerRadius=""3""
                    Background=""{TemplateBinding Background}""
                    BorderBrush=""{TemplateBinding BorderBrush}""
                    BorderThickness=""{TemplateBinding BorderThickness}"">
              <!-- PART_ContentHost is where the text is rendered; a TextBox
                   template without it shows nothing. -->
              <ScrollViewer x:Name=""PART_ContentHost"" Background=""{x:Null}""
                            Margin=""{TemplateBinding Padding}""
                            VerticalAlignment=""Center""
                            HorizontalScrollBarVisibility=""Hidden""
                            VerticalScrollBarVisibility=""Hidden""/>
            </Border>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType=""CheckBox"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Normal}""/>
      <Setter Property=""FontSize"" Value=""12""/>
      <Setter Property=""Margin"" Value=""0,3,0,0""/>
      <Setter Property=""Focusable"" Value=""False""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""CheckBox"">
            <!-- Transparent, not null: the whole row should be clickable. -->
            <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
              <Border x:Name=""Box"" Width=""14"" Height=""14"" CornerRadius=""3""
                      VerticalAlignment=""Center""
                      Background=""{StaticResource Bg.Field}""
                      BorderBrush=""{StaticResource Edge}"" BorderThickness=""1"">
                <Path x:Name=""Tick"" Visibility=""Collapsed""
                      Data=""M 0,3.5 L 2.5,6 L 7,0.5""
                      Stroke=""{StaticResource Text.Normal}"" StrokeThickness=""1.6""
                      HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
              </Border>
              <ContentPresenter Margin=""6,0,0,0"" VerticalAlignment=""Center""/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property=""IsChecked"" Value=""True"">
                <Setter TargetName=""Tick"" Property=""Visibility"" Value=""Visible""/>
                <Setter TargetName=""Box"" Property=""Background"" Value=""{StaticResource Bg.Press}""/>
              </Trigger>
              <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""Box"" Property=""BorderBrush"" Value=""{StaticResource Text.Head}""/>
              </Trigger>
              <Trigger Property=""IsEnabled"" Value=""False"">
                <Setter Property=""Foreground"" Value=""{StaticResource Text.Off}""/>
                <Setter TargetName=""Box"" Property=""Opacity"" Value=""0.45""/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <Style TargetType=""RadioButton"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Normal}""/>
      <Setter Property=""FontSize"" Value=""12""/>
      <Setter Property=""Margin"" Value=""0,0,12,0""/>
      <Setter Property=""Focusable"" Value=""False""/>
      <Setter Property=""Template"">
        <Setter.Value>
          <ControlTemplate TargetType=""RadioButton"">
            <StackPanel Orientation=""Horizontal"" Background=""Transparent"">
              <Border x:Name=""Ring"" Width=""14"" Height=""14"" CornerRadius=""7""
                      VerticalAlignment=""Center""
                      Background=""{StaticResource Bg.Field}""
                      BorderBrush=""{StaticResource Edge}"" BorderThickness=""1"">
                <Ellipse x:Name=""Dot"" Width=""6"" Height=""6"" Visibility=""Collapsed""
                         Fill=""{StaticResource Text.Normal}""
                         HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
              </Border>
              <ContentPresenter Margin=""6,0,0,0"" VerticalAlignment=""Center""/>
            </StackPanel>
            <ControlTemplate.Triggers>
              <Trigger Property=""IsChecked"" Value=""True"">
                <Setter TargetName=""Dot"" Property=""Visibility"" Value=""Visible""/>
                <Setter TargetName=""Ring"" Property=""Background"" Value=""{StaticResource Bg.Press}""/>
              </Trigger>
              <Trigger Property=""IsMouseOver"" Value=""True"">
                <Setter TargetName=""Ring"" Property=""BorderBrush"" Value=""{StaticResource Text.Head}""/>
              </Trigger>
              <Trigger Property=""IsEnabled"" Value=""False"">
                <Setter Property=""Foreground"" Value=""{StaticResource Text.Off}""/>
                <Setter TargetName=""Ring"" Property=""Opacity"" Value=""0.45""/>
              </Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>

    <!-- Centred modal card shared by every popup -->
    <Style x:Key=""Popup"" TargetType=""Border"">
      <Setter Property=""HorizontalAlignment"" Value=""Center""/>
      <Setter Property=""VerticalAlignment"" Value=""Center""/>
      <Setter Property=""CornerRadius"" Value=""8""/>
      <Setter Property=""Padding"" Value=""16,14""/>
      <Setter Property=""Background"" Value=""{StaticResource Bg.Alert}""/>
      <Setter Property=""BorderBrush"" Value=""{StaticResource Edge}""/>
      <Setter Property=""BorderThickness"" Value=""1""/>
    </Style>

    <Style x:Key=""PopupTitle"" TargetType=""TextBlock"">
      <Setter Property=""Foreground"" Value=""{StaticResource Text.Head}""/>
      <Setter Property=""FontSize"" Value=""14""/>
      <Setter Property=""FontWeight"" Value=""Bold""/>
      <Setter Property=""Margin"" Value=""0,0,0,8""/>
    </Style>
  </Grid.Resources>

  <!-- ══ Main panel ══════════════════════════════════════════════════════ -->
  <!-- Background must stay null: this stretches the full screen height, and a
       themed default would make the whole right-hand column hit-testable and
       swallow clicks that belong to the game. -->
  <!-- Focusable=False: a ScrollViewer takes focus and keyboard-scrolls by
       default, which would eat arrow and page keys meant for the game.
       The RenderTransform is what the header drag moves. Render, not layout,
       so the panel keeps its right-anchored slot and only the drawn position
       changes - and Noesis hit-tests through the transform, so it still takes
       clicks where you see it. -->
  <ScrollViewer x:Name=""PanelScroll""
                HorizontalAlignment=""Right"" VerticalAlignment=""Stretch""
                Margin=""10"" Width=""356"" VerticalScrollBarVisibility=""Auto""
                HorizontalScrollBarVisibility=""Disabled"" Background=""{x:Null}""
                Focusable=""False""
                Visibility=""{Binding PanelVisibility}"">
    <ScrollViewer.RenderTransform>
      <TranslateTransform/>
    </ScrollViewer.RenderTransform>
    <Border x:Name=""Panel"" VerticalAlignment=""Top""
            CornerRadius=""8"" Padding=""12"" Margin=""0,0,4,0""
            Background=""{StaticResource Bg.Panel}""
            BorderBrush=""{StaticResource Edge}"" BorderThickness=""1"">
      <StackPanel>

        <!-- Header. Doubles as the drag handle, wired up in NoesisOverlay -
             Background must be Transparent (not null) for the empty parts of
             the row to hit-test, which is most of what there is to grab. -->
        <Grid x:Name=""DragBar"" Background=""Transparent"">
          <Grid.ColumnDefinitions>
            <ColumnDefinition Width=""Auto""/>
            <ColumnDefinition Width=""Auto""/>
            <ColumnDefinition Width=""*""/>
            <ColumnDefinition Width=""Auto""/>
          </Grid.ColumnDefinitions>
          <Button Grid.Column=""0"" Style=""{StaticResource Btn}"" Margin=""0,0,6,0""
                  Padding=""4,0"" Content=""{Binding ExpandGlyph}""
                  Command=""{Binding ToggleExpandedCommand}""/>
          <TextBlock Grid.Column=""1"" Text=""&#x25cf;"" FontSize=""13"" Margin=""0,0,6,0""
                     VerticalAlignment=""Center""
                     Foreground=""{Binding SyncDotBrush}""
                     Visibility=""{Binding SyncDotVisibility}""/>
          <!-- The toggle is the one thing a new player cannot discover from the
               panel itself, so it is named in the title row and stays visible
               when the body is collapsed. -->
          <StackPanel Grid.Column=""2"" Orientation=""Horizontal"" VerticalAlignment=""Center"">
            <TextBlock Text=""{Binding VersionText}"" FontWeight=""Bold""
                       FontSize=""13"" VerticalAlignment=""Center""
                       Foreground=""{StaticResource Text.Head}""/>
            <TextBlock Text=""Ctrl+F9 to show/hide menu"" Margin=""8,0,0,0"" VerticalAlignment=""Center""
                       Style=""{StaticResource Dim}""/>
          </StackPanel>
          <TextBlock Grid.Column=""3"" Text=""PvP"" VerticalAlignment=""Center""
                     Style=""{StaticResource Warn}""
                     Visibility=""{Binding PvPBadgeVisibility}""/>
        </Grid>

        <StackPanel Visibility=""{Binding BodyVisibility}"">

          <!-- Workshop update prompt. Resubscribing is the advice because Steam
               cannot swap the DLL under a running game, and unsub/resub is the
               one action that reliably forces it. -->
          <Border Margin=""0,8,0,0"" CornerRadius=""4"" Padding=""8""
                  Background=""{StaticResource Bg.Alert}""
                  Visibility=""{Binding UpdateBannerVisibility}"">
            <StackPanel>
              <TextBlock Style=""{StaticResource Warn}""
                         Text=""Update available. Resubscribe to mod on workshop to install right away""/>
              <Button Style=""{StaticResource Btn}"" Content=""Open Workshop Page""
                      Command=""{Binding OpenWorkshopCommand}""
                      Visibility=""{Binding WorkshopButtonVisibility}""/>
            </StackPanel>
          </Border>

          <!-- Fatal init error replaces the body: a lobby button that cannot
               work is worse than no button. -->
          <Border Margin=""0,8,0,0"" CornerRadius=""4"" Padding=""8""
                  Background=""{StaticResource Bg.Alert}""
                  Visibility=""{Binding FatalNoticeVisibility}"">
            <StackPanel>
              <TextBlock Text=""MOD FAILED TO LOAD"" FontWeight=""Bold""
                         Foreground=""#FFFF6666""/>
              <TextBlock Text=""{Binding FatalMessage}"" Foreground=""#FFFF6666"" Margin=""0,4,0,0""/>
              <TextBlock Style=""{StaticResource Dim}"" Margin=""0,4,0,0""
                         Text=""Multiplayer is disabled for this session. Check BepInEx/LogOutput.log for the full error and report it on the Workshop page.""/>
            </StackPanel>
          </Border>

          <StackPanel Visibility=""{Binding SectionsVisibility}"">

            <!-- ── NETWORK ─────────────────────────────────────────────── -->
            <TextBlock Text=""NETWORK"" FontWeight=""Bold"" FontSize=""11"" Margin=""0,10,0,4""
                       Foreground=""{StaticResource Text.Head}""/>

            <Grid>
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""*""/>
                <ColumnDefinition Width=""Auto""/>
              </Grid.ColumnDefinitions>
              <TextBlock Grid.Column=""0"" Text=""{Binding ModeText}""/>
              <TextBlock Grid.Column=""1"" Text=""{Binding StatusText}""
                         Foreground=""{Binding StatusBrush}""/>
            </Grid>
            <TextBlock Text=""{Binding DetailText}"" Style=""{StaticResource Dim}""
                       Visibility=""{Binding DetailVisibility}""/>
            <TextBlock Text=""{Binding PeerText}"" Style=""{StaticResource Dim}""
                       Visibility=""{Binding PeerVisibility}""/>

            <!-- Steam transport -->
            <StackPanel Visibility=""{Binding SteamVisibility}"">
              <StackPanel Visibility=""{Binding ConnectedButtonsVisibility}"">
                <Button Style=""{StaticResource Btn}"" Content=""Disconnect""
                        Command=""{Binding DisconnectCommand}""/>
                <Button Style=""{StaticResource Btn}"" Content=""Send State &amp; Wait""
                        Command=""{Binding SendStateCommand}""
                        Visibility=""{Binding SendStateVisibility}""/>
                <TextBlock Style=""{StaticResource Dim}"" Margin=""0,4,0,0"" TextWrapping=""Wrap""
                           Visibility=""{Binding SendStateHintVisibility}""
                           Text=""Start or load a mission, then send it to the other player.""/>
              </StackPanel>

              <StackPanel Visibility=""{Binding LobbyOwnerButtonsVisibility}"">
                <Grid Margin=""0,4,0,0"">
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width=""Auto""/>
                    <ColumnDefinition Width=""*""/>
                  </Grid.ColumnDefinitions>
                  <TextBlock Grid.Column=""0"" Text=""Code:"" Style=""{StaticResource Dim}"" Margin=""0,0,6,0""/>
                  <TextBlock Grid.Column=""1"" Text=""{Binding ShareCode}"" FontWeight=""Bold""/>
                </Grid>
                <Button Style=""{StaticResource Btn}"" Content=""Copy Code""
                        Command=""{Binding CopyCodeCommand}""/>
                <Button Style=""{StaticResource Btn}"" Content=""Invite Friend""
                        Command=""{Binding InviteFriendCommand}""/>
                <Button Style=""{StaticResource Btn}"" Content=""Leave Lobby""
                        Command=""{Binding LeaveLobbyCommand}""/>
              </StackPanel>

              <StackPanel Visibility=""{Binding LobbyGuestButtonsVisibility}"">
                <Button Style=""{StaticResource Btn}"" Content=""Leave Lobby""
                        Command=""{Binding LeaveLobbyCommand}""/>
              </StackPanel>

              <StackPanel Visibility=""{Binding NoLobbyButtonsVisibility}"">
                <Button Style=""{StaticResource Btn}"" Content=""Host Lobby""
                        Command=""{Binding HostLobbyCommand}""/>
                <Button Style=""{StaticResource Btn}"" Content=""Join from Clipboard""
                        Command=""{Binding JoinClipboardCommand}""/>
                <TextBlock Style=""{StaticResource Dim}"" Margin=""0,4,0,0""
                           Text=""Copy a host's code, then join.""/>
              </StackPanel>
            </StackPanel>

            <!-- Direct IP (dev builds; the workshop build forces Steam) -->
            <StackPanel Visibility=""{Binding LiteNetVisibility}"">
              <Button Style=""{StaticResource Btn}"" Content=""{Binding LiteNetPrimaryText}""
                      Command=""{Binding LiteNetPrimaryCommand}""
                      Visibility=""{Binding LiteNetPrimaryVisibility}""/>
              <StackPanel Visibility=""{Binding ConnectedButtonsVisibility}"">
                <Button Style=""{StaticResource Btn}"" Content=""Disconnect""
                        Command=""{Binding DisconnectCommand}""/>
                <Button Style=""{StaticResource Btn}"" Content=""Send State &amp; Wait""
                        Command=""{Binding SendStateCommand}""
                        Visibility=""{Binding SendStateVisibility}""/>
                <TextBlock Style=""{StaticResource Dim}"" Margin=""0,4,0,0"" TextWrapping=""Wrap""
                           Visibility=""{Binding SendStateHintVisibility}""
                           Text=""Start or load a mission, then send it to the other player.""/>
              </StackPanel>
            </StackPanel>

            <TextBlock Text=""{Binding LobbyMsg}"" Style=""{StaticResource Dim}"" Margin=""0,4,0,0""
                       Visibility=""{Binding LobbyMsgVisibility}""/>

            <!-- Sticky banner for a failed or degraded sync -->
            <Border Margin=""0,6,0,0"" CornerRadius=""4"" Padding=""8""
                    Background=""{StaticResource Bg.Alert}""
                    Visibility=""{Binding SyncIssueVisibility}"">
              <StackPanel>
                <TextBlock Text=""{Binding SyncIssueText}"" Foreground=""{Binding SyncIssueBrush}""/>
                <TextBlock Text=""{Binding SyncIssueHint}"" Style=""{StaticResource Dim}""
                           Visibility=""{Binding SyncIssueHintVisibility}""/>
              </StackPanel>
            </Border>

            <TextBlock Text=""{Binding SyncStateText}"" Foreground=""{Binding SyncStateBrush}""
                       Margin=""0,4,0,0"" Visibility=""{Binding SyncStateVisibility}""/>
            <TextBlock Text=""Receiving scene..."" Style=""{StaticResource Warn}""
                       Visibility=""{Binding ReceivingVisibility}""/>

            <!-- ── TIME CONTROL ────────────────────────────────────────── -->
            <TextBlock Text=""TIME CONTROL"" FontWeight=""Bold"" FontSize=""11"" Margin=""0,12,0,4""
                       Foreground=""{StaticResource Text.Head}""/>
            <TextBlock Text=""{Binding TimeText}""/>
            <Grid Margin=""0,4,0,0"">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""Auto""/>
                <ColumnDefinition Width=""*""/>
                <ColumnDefinition Width=""Auto""/>
              </Grid.ColumnDefinitions>
              <Button Grid.Column=""0"" Style=""{StaticResource Btn}"" Content=""&lt;&lt;"" Width=""44""
                      Command=""{Binding TimeDecreaseCommand}""/>
              <Button Grid.Column=""1"" Style=""{StaticResource Btn}"" Margin=""6,2,6,0""
                      Content=""{Binding PauseButtonText}""
                      Command=""{Binding TimeTogglePauseCommand}""/>
              <Button Grid.Column=""2"" Style=""{StaticResource Btn}"" Content=""&gt;&gt;"" Width=""44""
                      Command=""{Binding TimeIncreaseCommand}""/>
            </Grid>
            <TextBlock Text=""{Binding TimeWaitText}"" Style=""{StaticResource Warn}""
                       Visibility=""{Binding TimeWaitVisibility}""/>

            <!-- ── SETTINGS ────────────────────────────────────────────── -->
            <Button Style=""{StaticResource Fold}"" Command=""{Binding ToggleSettingsCommand}"">
              <Grid>
                <TextBlock Text=""SETTINGS"" FontWeight=""Bold"" FontSize=""11""
                           Foreground=""{StaticResource Text.Head}""/>
                <TextBlock Text=""{Binding SettingsGlyph}"" HorizontalAlignment=""Right""
                           Foreground=""{StaticResource Text.Head}""/>
              </Grid>
            </Button>
            <StackPanel Margin=""4,4,0,0"" Visibility=""{Binding SettingsVisibility}"">
              <StackPanel Orientation=""Horizontal"" IsEnabled=""{Binding ModeUnlocked}"">
                <TextBlock Text=""Mode"" Width=""64"" VerticalAlignment=""Center""/>
                <RadioButton GroupName=""mp_mode"" Content=""PvP""
                             IsChecked=""{Binding IsPvP, Mode=TwoWay}""/>
                <RadioButton GroupName=""mp_mode"" Content=""Co-op""
                             IsChecked=""{Binding IsCoop, Mode=TwoWay}""/>
              </StackPanel>
              <TextBlock Text=""Leave the lobby to change mode"" Style=""{StaticResource Dim}""
                         Visibility=""{Binding ModeLockedNoticeVisibility}""/>

              <CheckBox Content=""Time vote (host)"" IsChecked=""{Binding TimeVote, Mode=TwoWay}""/>

              <TextBlock Text=""Sync state (applies live)"" Style=""{StaticResource Dim}"" Margin=""0,8,0,2""/>
              <Grid>
                <Grid.ColumnDefinitions>
                  <ColumnDefinition Width=""78""/>
                  <ColumnDefinition Width=""*""/>
                </Grid.ColumnDefinitions>
                <Grid.RowDefinitions>
                  <RowDefinition Height=""Auto""/>
                  <RowDefinition Height=""Auto""/>
                  <RowDefinition Height=""Auto""/>
                </Grid.RowDefinitions>
                <TextBlock Grid.Row=""0"" Grid.Column=""0"" Text=""Unit Hz"" VerticalAlignment=""Center""/>
                <TextBox   Grid.Row=""0"" Grid.Column=""1"" Style=""{StaticResource Field}"" Margin=""0,1""
                           Text=""{Binding UnitHzText, Mode=TwoWay}""/>
                <TextBlock Grid.Row=""1"" Grid.Column=""0"" Text=""Missile Hz"" VerticalAlignment=""Center""/>
                <TextBox   Grid.Row=""1"" Grid.Column=""1"" Style=""{StaticResource Field}"" Margin=""0,1""
                           Text=""{Binding MissileHzText, Mode=TwoWay}""/>
                <TextBlock Grid.Row=""2"" Grid.Column=""0"" Text=""Damage s"" VerticalAlignment=""Center""/>
                <TextBox   Grid.Row=""2"" Grid.Column=""1"" Style=""{StaticResource Field}"" Margin=""0,1""
                           Text=""{Binding DamageIntervalText, Mode=TwoWay}""/>
              </Grid>

              <TextBlock Text=""Shared picture (co-op)"" Style=""{StaticResource Dim}"" Margin=""0,8,0,2""/>
              <StackPanel IsEnabled=""{Binding SharedPictureEnabled}"">
                <CheckBox Content=""Contacts &amp; track numbers""
                          IsChecked=""{Binding ContactSync, Mode=TwoWay}""/>
                <CheckBox Content=""Map markers""
                          IsChecked=""{Binding DrawingSync, Mode=TwoWay}""/>
              </StackPanel>
              <TextBlock Style=""{StaticResource Dim}"" Visibility=""{Binding PvPIntelNoticeVisibility}""
                         Text=""Co-op only - sharing these would give away intel""/>

              <!-- Top level, not under Advanced: a consent setting has to be
                   findable without expanding a disclosure triangle. -->
              <TextBlock Text=""Diagnostics"" Style=""{StaticResource Dim}"" Margin=""0,8,0,2""/>
              <CheckBox Content=""Share diagnostics (helps fix bugs)""
                        IsChecked=""{Binding ShareDiagnostics, Mode=TwoWay}""/>
              <TextBlock Text=""{Binding DiagnosticsIdText}"" Style=""{StaticResource Dim}""/>

              <Button Style=""{StaticResource Fold}"" Command=""{Binding ToggleAdvancedCommand}"">
                <Grid>
                  <TextBlock Text=""Advanced"" FontSize=""11"" Foreground=""{StaticResource Text.Head}""/>
                  <TextBlock Text=""{Binding AdvancedGlyph}"" HorizontalAlignment=""Right""
                             Foreground=""{StaticResource Text.Head}""/>
                </Grid>
              </Button>
              <StackPanel Margin=""4,2,0,0"" Visibility=""{Binding AdvancedVisibility}"">
                <CheckBox Content=""Verbose logging"" IsChecked=""{Binding VerboseLogging, Mode=TwoWay}""/>
                <Button Style=""{StaticResource Btn}"" Content=""Reset to defaults""
                        IsEnabled=""{Binding ModeUnlocked}""
                        Command=""{Binding ResetSettingsCommand}""/>
              </StackPanel>
            </StackPanel>

            <!-- ── SYNC HEALTH ─────────────────────────────────────────── -->
            <StackPanel Visibility=""{Binding SyncHealthVisibility}"">
              <TextBlock Text=""SYNC HEALTH"" FontWeight=""Bold"" FontSize=""11"" Margin=""0,12,0,2""
                         Foreground=""{StaticResource Text.Head}""/>
              <TextBlock Text=""Tip: press Ctrl+F10 to force a resync"" Style=""{StaticResource Dim}""/>

              <Button Style=""{StaticResource Fold}"" Command=""{Binding ToggleDetailsCommand}"">
                <Grid>
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width=""Auto""/>
                    <ColumnDefinition Width=""*""/>
                    <ColumnDefinition Width=""Auto""/>
                  </Grid.ColumnDefinitions>
                  <TextBlock Grid.Column=""0"" Text=""{Binding DetailsGlyph}"" Margin=""0,0,6,0""
                             Foreground=""{StaticResource Text.Head}""/>
                  <TextBlock Grid.Column=""1"" Text=""Details"" FontSize=""11""
                             Foreground=""{StaticResource Text.Head}""/>
                  <TextBlock Grid.Column=""2"" Text=""&#x25cf;"" Foreground=""{Binding DetailsDotBrush}""/>
                </Grid>
              </Button>

              <StackPanel Margin=""4,2,0,0"" Visibility=""{Binding DetailsVisibility}"">
                <TextBlock Text=""{Binding RttText}"" Style=""{StaticResource Dim}""/>

                <Button Style=""{StaticResource Fold}"" Command=""{Binding ToggleUnitsCommand}"">
                  <Grid>
                    <Grid.ColumnDefinitions>
                      <ColumnDefinition Width=""Auto""/>
                      <ColumnDefinition Width=""*""/>
                      <ColumnDefinition Width=""Auto""/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column=""0"" Text=""{Binding UnitsGlyph}"" Margin=""0,0,6,0""
                               Foreground=""{StaticResource Text.Head}""/>
                    <TextBlock Grid.Column=""1"" Text=""Units"" FontSize=""11""
                               Foreground=""{StaticResource Text.Head}""/>
                    <TextBlock Grid.Column=""2"" Text=""&#x25cf;"" Foreground=""{Binding UnitsDotBrush}""/>
                  </Grid>
                </Button>
                <StackPanel Margin=""4,2,0,0"" Visibility=""{Binding UnitsVisibility}"">
                  <TextBlock Text=""{Binding UnitCountsText}""/>
                  <TextBlock Text=""{Binding ShipDriftText}"" Foreground=""{Binding ShipDriftBrush}"" FontSize=""11""/>
                  <TextBlock Text=""{Binding AirDriftText}"" Foreground=""{Binding AirDriftBrush}"" FontSize=""11""/>
                  <TextBlock Text=""{Binding PredictErrText}"" Style=""{StaticResource Dim}""/>
                </StackPanel>

                <Button Style=""{StaticResource Fold}"" Command=""{Binding ToggleProjectilesCommand}"">
                  <Grid>
                    <TextBlock Text=""Projectiles"" FontSize=""11"" Foreground=""{StaticResource Text.Head}""/>
                    <TextBlock Text=""{Binding ProjectilesGlyph}"" HorizontalAlignment=""Right""
                               Foreground=""{StaticResource Text.Head}""/>
                  </Grid>
                </Button>
                <TextBlock Margin=""4,2,0,0"" Text=""{Binding ProjectilesText}""
                           Visibility=""{Binding ProjectilesVisibility}""/>
              </StackPanel>
            </StackPanel>

            <!-- ── NET v2 ──────────────────────────────────────────────── -->
            <TextBlock Text=""NET v2"" FontWeight=""Bold"" FontSize=""11"" Margin=""0,12,0,2""
                       Foreground=""{StaticResource Text.Head}""/>
            <TextBlock Text=""{Binding ProtocolText}"" Style=""{StaticResource Dim}""/>
            <StackPanel Visibility=""{Binding Net2DetailVisibility}"">
              <TextBlock Text=""{Binding RatesText}"" Style=""{StaticResource Dim}""/>
              <TextBlock Text=""{Binding LossText}"" Style=""{StaticResource Dim}""/>
              <TextBlock Text=""{Binding SendFrameText}"" Style=""{StaticResource Dim}""/>
              <TextBlock Text=""{Binding ReplicasText}"" Style=""{StaticResource Dim}""/>

              <Button Style=""{StaticResource Fold}"" Command=""{Binding ToggleCountersCommand}"">
                <Grid>
                  <TextBlock Text=""Counters"" FontSize=""11"" Foreground=""{StaticResource Text.Head}""/>
                  <TextBlock Text=""{Binding CountersGlyph}"" HorizontalAlignment=""Right""
                             Foreground=""{StaticResource Text.Head}""/>
                </Grid>
              </Button>
              <ItemsControl Margin=""4,2,0,0"" ItemsSource=""{Binding Counters}""
                            Visibility=""{Binding CountersVisibility}"">
                <ItemsControl.ItemTemplate>
                  <DataTemplate>
                    <TextBlock Text=""{Binding}"" Style=""{StaticResource Dim}""/>
                  </DataTemplate>
                </ItemsControl.ItemTemplate>
              </ItemsControl>
            </StackPanel>

          </StackPanel>

          <!-- Footer. Outside SectionsVisibility on purpose: a failed init hides
               everything above, and that is precisely when someone needs to ask
               for help. -->
          <Border Margin=""0,12,0,0"" Padding=""0,8,0,0""
                  BorderBrush=""{StaticResource Edge}"" BorderThickness=""0,1,0,0"">
            <StackPanel>
              <Button Style=""{StaticResource Btn}"" Content=""Join Discord""
                      Command=""{Binding JoinDiscordCommand}""/>
              <TextBlock Text=""{Binding DiscordMsg}"" Style=""{StaticResource Dim}""
                         Margin=""0,4,0,0"" Visibility=""{Binding DiscordMsgVisibility}""/>
            </StackPanel>
          </Border>
        </StackPanel>
      </StackPanel>
    </Border>
  </ScrollViewer>

  <!-- ══ Popups - shown even when the panel is closed ════════════════════ -->

  <Border Style=""{StaticResource Popup}"" Width=""340"" Visibility=""{Binding TimeVoteVisibility}"">
    <StackPanel>
      <TextBlock Style=""{StaticResource PopupTitle}"" Text=""Time Change Request""/>
      <TextBlock Text=""{Binding TimeVoteText}"" Style=""{StaticResource Warn}""/>
      <Grid Margin=""0,10,0,0"">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width=""*""/>
          <ColumnDefinition Width=""*""/>
        </Grid.ColumnDefinitions>
        <Button Grid.Column=""0"" Style=""{StaticResource Btn}"" Content=""Agree"" Margin=""0,0,4,0""
                Command=""{Binding VoteAgreeCommand}""/>
        <Button Grid.Column=""1"" Style=""{StaticResource Btn}"" Content=""Decline"" Margin=""4,0,0,0""
                Command=""{Binding VoteDeclineCommand}""/>
      </Grid>
    </StackPanel>
  </Border>

  <Border Style=""{StaticResource Popup}"" Width=""400"" Visibility=""{Binding ConnLostVisibility}"">
    <StackPanel>
      <TextBlock Style=""{StaticResource PopupTitle}"" Text=""Connection Lost""/>
      <TextBlock Text=""{Binding ConnLostStatus}"" Style=""{StaticResource Warn}""/>
      <TextBlock Style=""{StaticResource Dim}"" Margin=""0,6,0,0""
                 Text=""The game is paused for both players and will resume automatically once the session has synced again.""/>
      <StackPanel Margin=""0,10,0,0"" Visibility=""{Binding ConnLostButtonsVisibility}"">
        <Button Style=""{StaticResource Btn}"" Content=""Reconnect Now""
                Command=""{Binding ReconnectNowCommand}""
                Visibility=""{Binding ReconnectNowVisibility}""/>
        <Button Style=""{StaticResource Btn}"" Content=""Re-invite""
                Command=""{Binding ReinviteCommand}""
                Visibility=""{Binding ReinviteVisibility}""/>
        <Button Style=""{StaticResource Btn}"" Content=""{Binding AbandonText}""
                Command=""{Binding AbandonSessionCommand}""/>
      </StackPanel>
    </StackPanel>
  </Border>

  <Border Style=""{StaticResource Popup}"" Width=""440"" Visibility=""{Binding MismatchVisibility}"">
    <StackPanel>
      <TextBlock Style=""{StaticResource PopupTitle}"" Text=""Mod Version Mismatch""/>
      <TextBlock Text=""{Binding MismatchNotice}"" Style=""{StaticResource Warn}""/>
      <TextBlock Style=""{StaticResource Dim}"" Margin=""0,6,0,0""
                 Text=""Steam does not always auto-update Workshop mods. Whichever of you is outdated: unsubscribe from Seapower Multiplayer on the Steam Workshop, resubscribe, then restart the game. If unsure, both players should do it.""/>
      <Grid Margin=""0,10,0,0"">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width=""*""/>
          <ColumnDefinition Width=""*""/>
        </Grid.ColumnDefinitions>
        <Button Grid.Column=""0"" Style=""{StaticResource Btn}"" Content=""Open Workshop Page""
                Margin=""0,0,4,0"" Command=""{Binding OpenWorkshopCommand}""
                Visibility=""{Binding WorkshopButtonVisibility}""/>
        <Button Grid.Column=""1"" Style=""{StaticResource Btn}"" Content=""Dismiss"" Margin=""4,0,0,0""
                Command=""{Binding DismissMismatchCommand}""/>
      </Grid>
    </StackPanel>
  </Border>

  <Border Style=""{StaticResource Popup}"" Width=""440"" Visibility=""{Binding FatalPopupVisibility}"">
    <StackPanel>
      <TextBlock Style=""{StaticResource PopupTitle}"" Text=""Multiplayer Mod Failed to Load""/>
      <TextBlock Text=""{Binding FatalMessage}"" Foreground=""#FFFF6666""/>
      <TextBlock Style=""{StaticResource Dim}"" Margin=""0,6,0,0""
                 Text=""Multiplayer is disabled for this session. The game itself is unaffected. Check BepInEx/LogOutput.log for the full error and report it on the Workshop page.""/>
      <Button Style=""{StaticResource Btn}"" Content=""Dismiss"" Margin=""0,10,0,0""
              Command=""{Binding DismissFatalCommand}""/>
    </StackPanel>
  </Border>

  <!-- One-time diagnostics consent. Vagueness here is what makes people decline,
       so it enumerates exactly what does and does not leave the machine. -->
  <Border Style=""{StaticResource Popup}"" Width=""460"" Visibility=""{Binding ConsentVisibility}"">
    <StackPanel>
      <TextBlock Style=""{StaticResource PopupTitle}"" Text=""Share Diagnostics?""/>
      <TextBlock TextWrapping=""Wrap""
                 Text=""Sending anonymous diagnostics helps fix desyncs and connection problems.""/>
      <TextBlock Style=""{StaticResource Dim}"" Margin=""0,8,0,0"" TextWrapping=""Wrap""
                 Text=""What is sent: this mod's log messages, connection quality (ping, packet loss, bandwidth), frame rate, replica drift, and your mission, mode and version.""/>
      <TextBlock Style=""{StaticResource Dim}"" Margin=""0,4,0,0"" TextWrapping=""Wrap""
                 Text=""What is not sent: your name, your Steam ID, your IP address, chat or saves. Steam IDs, names and file paths are scrubbed before anything leaves your PC.""/>
      <TextBlock Style=""{StaticResource Dim}"" Margin=""0,4,0,0"" TextWrapping=""Wrap""
                 Text=""You are identified only by a random ID stored on this PC. Data is only sent while you are in a multiplayer session, and is deleted after 30 days. You can turn this off any time in SETTINGS.""/>
      <Grid Margin=""0,10,0,0"">
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width=""*""/>
          <ColumnDefinition Width=""*""/>
        </Grid.ColumnDefinitions>
        <Button Grid.Column=""0"" Style=""{StaticResource Btn}"" Content=""Enable"" Margin=""0,0,4,0""
                Command=""{Binding EnableDiagnosticsCommand}""/>
        <Button Grid.Column=""1"" Style=""{StaticResource Btn}"" Content=""No thanks"" Margin=""4,0,0,0""
                Command=""{Binding DeclineDiagnosticsCommand}""/>
      </Grid>
    </StackPanel>
  </Border>

  <!-- Ally lock: fires from ordinary clicking, so it auto-expires and never
       needs dismissing. Kept low and clear of the unit panels. -->
  <Border Width=""470"" CornerRadius=""6"" Padding=""12,8""
          HorizontalAlignment=""Center"" VerticalAlignment=""Bottom"" Margin=""0,0,0,110""
          Background=""{StaticResource Bg.Alert}""
          BorderBrush=""{StaticResource Edge}"" BorderThickness=""1""
          Visibility=""{Binding AllyLockVisibility}"">
    <StackPanel>
      <TextBlock Text=""{Binding AllyLockText}"" Style=""{StaticResource Warn}""
                 HorizontalAlignment=""Center""/>
      <TextBlock Text=""Ask them to deselect it, or take a different unit.""
                 Style=""{StaticResource Dim}"" HorizontalAlignment=""Center""/>
    </StackPanel>
  </Border>

</Grid>";
    }
}
