import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:provider/provider.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:file_picker/file_picker.dart';
import '../providers/app_provider.dart';
import '../services/app_config_service.dart';
import '../widgets/login_dialog.dart';
import 'main_screen.dart';

/// First-run setup wizard that guides the user through:
/// 1. Welcome
/// 2. sldl executable path
/// 3. Soulseek login
/// 4. Spotify setup (optional)
/// 5. YouTube setup (optional)
/// 6. Done
class SetupWizardScreen extends StatefulWidget {
  const SetupWizardScreen({super.key});

  @override
  State<SetupWizardScreen> createState() => _SetupWizardScreenState();
}

class _SetupWizardScreenState extends State<SetupWizardScreen> {
  final _pageCtrl = PageController();
  int _page = 0;
  static const _totalPages = 6;

  final _sldlPathCtrl = TextEditingController();
  final _usernameCtrl = TextEditingController();
  final _passwordCtrl = TextEditingController();
  final _spotifyIdCtrl = TextEditingController();
  final _spotifySecretCtrl = TextEditingController();
  final _youtubeKeyCtrl = TextEditingController();

  bool _obscurePassword = true;

  @override
  void initState() {
    super.initState();
    final provider = context.read<AppProvider>();
    _usernameCtrl.text = provider.config.username ?? '';
    _passwordCtrl.text = provider.config.password ?? '';
    _spotifyIdCtrl.text = provider.config.spotifyId ?? '';
    _spotifySecretCtrl.text = provider.config.spotifySecret ?? '';
    _youtubeKeyCtrl.text = provider.config.youtubeKey ?? '';

    // Pre-fill sldl path if detected
    final existing = provider.sldlExecutablePath;
    if (existing != null) _sldlPathCtrl.text = existing;
  }

  @override
  void dispose() {
    _pageCtrl.dispose();
    _sldlPathCtrl.dispose();
    _usernameCtrl.dispose();
    _passwordCtrl.dispose();
    _spotifyIdCtrl.dispose();
    _spotifySecretCtrl.dispose();
    _youtubeKeyCtrl.dispose();
    super.dispose();
  }

  void _next() {
    if (_page < _totalPages - 1) {
      _pageCtrl.nextPage(
          duration: const Duration(milliseconds: 300), curve: Curves.easeInOut);
      setState(() => _page++);
    }
  }

  void _back() {
    if (_page > 0) {
      _pageCtrl.previousPage(
          duration: const Duration(milliseconds: 300), curve: Curves.easeInOut);
      setState(() => _page--);
    }
  }

  Future<void> _finish() async {
    final provider = context.read<AppProvider>();
    final appConfig = provider.appConfigService;

    // Save sldl path
    if (_sldlPathCtrl.text.isNotEmpty) {
      provider.updateSldlPath(_sldlPathCtrl.text.trim());
    }

    // Save credentials
    if (_usernameCtrl.text.isNotEmpty && _passwordCtrl.text.isNotEmpty) {
      await provider.saveCredentials(
          _usernameCtrl.text.trim(), _passwordCtrl.text);
    }

    // Save Spotify/YouTube settings
    provider.config.spotifyId =
        _spotifyIdCtrl.text.trim().isEmpty ? null : _spotifyIdCtrl.text.trim();
    provider.config.spotifySecret = _spotifySecretCtrl.text.trim().isEmpty
        ? null
        : _spotifySecretCtrl.text.trim();
    provider.config.youtubeKey =
        _youtubeKeyCtrl.text.trim().isEmpty ? null : _youtubeKeyCtrl.text.trim();
    await provider.saveConfig(provider.config);

    await appConfig.setFirstRunDone();

    if (mounted) {
      Navigator.of(context).pushReplacement(
          MaterialPageRoute(builder: (_) => const MainScreen()));
    }
  }

  void _skip() {
    if (_page == _totalPages - 1) {
      _finish();
    } else {
      _next();
    }
  }

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Scaffold(
      body: Column(
        children: [
          // Progress indicator
          LinearProgressIndicator(
            value: (_page + 1) / _totalPages,
            minHeight: 4,
          ),

          // Page view
          Expanded(
            child: PageView(
              controller: _pageCtrl,
              physics: const NeverScrollableScrollPhysics(),
              children: [
                _WelcomePage(onNext: _next),
                _SldlPathPage(ctrl: _sldlPathCtrl, onNext: _next, onBack: _back),
                _SoulseekLoginPage(
                  usernameCtrl: _usernameCtrl,
                  passwordCtrl: _passwordCtrl,
                  obscurePassword: _obscurePassword,
                  onToggleObscure: () =>
                      setState(() => _obscurePassword = !_obscurePassword),
                  onNext: _next,
                  onBack: _back,
                ),
                _SpotifySetupPage(
                  idCtrl: _spotifyIdCtrl,
                  secretCtrl: _spotifySecretCtrl,
                  onNext: _next,
                  onSkip: _skip,
                  onBack: _back,
                ),
                _YouTubeSetupPage(
                  keyCtrl: _youtubeKeyCtrl,
                  onNext: _next,
                  onSkip: _skip,
                  onBack: _back,
                ),
                _DonePage(onFinish: _finish),
              ],
            ),
          ),
        ],
      ),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared wizard layout
// ─────────────────────────────────────────────────────────────────────────────

class _WizardPage extends StatelessWidget {
  final IconData icon;
  final Color iconColor;
  final String title;
  final String? subtitle;
  final List<Widget> children;
  final List<Widget>? actions;

  const _WizardPage({
    required this.icon,
    required this.iconColor,
    required this.title,
    this.subtitle,
    required this.children,
    this.actions,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 560),
        child: SingleChildScrollView(
          padding: const EdgeInsets.symmetric(horizontal: 32, vertical: 24),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              // Header
              Row(
                children: [
                  Container(
                    width: 48,
                    height: 48,
                    decoration: BoxDecoration(
                      color: iconColor.withOpacity(0.15),
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: Icon(icon, color: iconColor, size: 28),
                  ),
                  const SizedBox(width: 16),
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(title,
                            style: theme.textTheme.headlineSmall
                                ?.copyWith(fontWeight: FontWeight.bold)),
                        if (subtitle != null)
                          Text(subtitle!,
                              style: theme.textTheme.bodyMedium?.copyWith(
                                  color: theme.colorScheme.onSurfaceVariant)),
                      ],
                    ),
                  ),
                ],
              ),
              const SizedBox(height: 24),
              ...children,
              if (actions != null) ...[
                const SizedBox(height: 24),
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: actions!
                      .map((a) => Padding(
                            padding: const EdgeInsets.only(left: 8),
                            child: a,
                          ))
                      .toList(),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Page 0: Welcome
// ─────────────────────────────────────────────────────────────────────────────

class _WelcomePage extends StatelessWidget {
  final VoidCallback onNext;
  const _WelcomePage({required this.onNext});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return _WizardPage(
      icon: Icons.headphones,
      iconColor: theme.colorScheme.primary,
      title: 'Welcome to sldl UI',
      subtitle: 'A graphical interface for slsk-batchdl',
      children: [
        Text(
          'sldl UI wraps the slsk-batchdl command-line tool, making it easy to download music from Soulseek via Spotify playlists, YouTube playlists, CSV files, or direct search strings.',
          style: theme.textTheme.bodyMedium,
        ),
        const SizedBox(height: 16),
        _FeatureRow(icon: Icons.playlist_play, text: 'Spotify & YouTube playlist support'),
        _FeatureRow(icon: Icons.queue_music, text: 'Download queue with live progress'),
        _FeatureRow(icon: Icons.settings, text: 'Full configuration GUI'),
        _FeatureRow(icon: Icons.label_outline, text: 'Flexible name format builder'),
        const SizedBox(height: 16),
        Text(
          "This wizard will help you set up sldl UI in just a few steps. You can always change these settings later.",
          style: theme.textTheme.bodySmall
              ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
        ),
      ],
      actions: [
        FilledButton.icon(
          icon: const Icon(Icons.arrow_forward),
          label: const Text('Get Started'),
          onPressed: onNext,
        ),
      ],
    );
  }
}

class _FeatureRow extends StatelessWidget {
  final IconData icon;
  final String text;
  const _FeatureRow({required this.icon, required this.text});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Row(
        children: [
          Icon(icon, size: 18, color: Theme.of(context).colorScheme.primary),
          const SizedBox(width: 10),
          Text(text),
        ],
      ),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Page 1: sldl executable path
// ─────────────────────────────────────────────────────────────────────────────

class _SldlPathPage extends StatefulWidget {
  final TextEditingController ctrl;
  final VoidCallback onNext;
  final VoidCallback onBack;

  const _SldlPathPage(
      {required this.ctrl, required this.onNext, required this.onBack});

  @override
  State<_SldlPathPage> createState() => _SldlPathPageState();
}

class _SldlPathPageState extends State<_SldlPathPage> {
  bool _detecting = false;

  Future<void> _autoDetect() async {
    setState(() => _detecting = true);
    final provider = context.read<AppProvider>();
    final path = await provider.appConfigService.autoDetectSldlPath();
    if (path != null) {
      widget.ctrl.text = path;
    }
    if (mounted) setState(() => _detecting = false);
  }

  Future<void> _browse() async {
    final result = await FilePicker.platform.pickFiles(
      dialogTitle: 'Select sldl executable',
      type: FileType.custom,
      allowedExtensions: ['exe', ''],
    );
    if (result?.files.single.path != null) {
      widget.ctrl.text = result!.files.single.path!;
    }
  }

  @override
  Widget build(BuildContext context) {
    return _WizardPage(
      icon: Icons.terminal,
      iconColor: Colors.teal,
      title: 'sldl Executable',
      subtitle: 'Point sldl UI to the sldl binary',
      children: [
        Text(
          'sldl UI wraps the slsk-batchdl binary (sldl). You need to have it installed on your system.',
          style: Theme.of(context).textTheme.bodyMedium,
        ),
        const SizedBox(height: 8),
        Text(
          'Download sldl from: https://github.com/fiso64/slsk-batchdl/releases',
          style: Theme.of(context).textTheme.bodySmall?.copyWith(
            color: Theme.of(context).colorScheme.primary,
          ),
        ),
        const SizedBox(height: 16),
        Row(
          children: [
            Expanded(
              child: TextField(
                controller: widget.ctrl,
                decoration: InputDecoration(
                  labelText: 'sldl Executable Path',
                  hintText: 'e.g. C:\\sldl\\sldl.exe',
                  border: const OutlineInputBorder(),
                  suffixIcon: _detecting
                      ? const Padding(
                          padding: EdgeInsets.all(12),
                          child: SizedBox(
                              width: 16,
                              height: 16,
                              child: CircularProgressIndicator(strokeWidth: 2)),
                        )
                      : null,
                ),
              ),
            ),
            const SizedBox(width: 8),
            IconButton(
              icon: const Icon(Icons.folder_open),
              tooltip: 'Browse',
              onPressed: _browse,
            ),
          ],
        ),
        const SizedBox(height: 8),
        TextButton.icon(
          icon: const Icon(Icons.search, size: 16),
          label: const Text('Auto-detect'),
          onPressed: _detecting ? null : _autoDetect,
        ),
      ],
      actions: [
        TextButton(onPressed: widget.onBack, child: const Text('Back')),
        TextButton(onPressed: widget.onNext, child: const Text('Skip')),
        FilledButton(onPressed: widget.onNext, child: const Text('Next')),
      ],
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Page 2: Soulseek Login
// ─────────────────────────────────────────────────────────────────────────────

class _SoulseekLoginPage extends StatelessWidget {
  final TextEditingController usernameCtrl;
  final TextEditingController passwordCtrl;
  final bool obscurePassword;
  final VoidCallback onToggleObscure;
  final VoidCallback onNext;
  final VoidCallback onBack;

  const _SoulseekLoginPage({
    required this.usernameCtrl,
    required this.passwordCtrl,
    required this.obscurePassword,
    required this.onToggleObscure,
    required this.onNext,
    required this.onBack,
  });

  @override
  Widget build(BuildContext context) {
    return _WizardPage(
      icon: Icons.lock_outline,
      iconColor: Colors.indigo,
      title: 'Soulseek Login',
      subtitle: 'Enter your Soulseek credentials',
      children: [
        Container(
          padding: const EdgeInsets.all(10),
          margin: const EdgeInsets.only(bottom: 12),
          decoration: BoxDecoration(
            color: Theme.of(context).colorScheme.secondaryContainer.withOpacity(0.5),
            borderRadius: BorderRadius.circular(8),
          ),
          child: const Text(
            'Tip: It is recommended to use a separate Soulseek account for sldl to avoid connection problems. '
            'sldl does NOT share your music folders — please also run a regular Soulseek client to share your collection.',
            style: TextStyle(fontSize: 13),
          ),
        ),
        TextField(
          controller: usernameCtrl,
          decoration: const InputDecoration(
            labelText: 'Soulseek Username',
            prefixIcon: Icon(Icons.person_outline),
            border: OutlineInputBorder(),
          ),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: passwordCtrl,
          obscureText: obscurePassword,
          decoration: InputDecoration(
            labelText: 'Soulseek Password',
            prefixIcon: const Icon(Icons.lock_outline),
            border: const OutlineInputBorder(),
            suffixIcon: IconButton(
              icon: Icon(
                  obscurePassword ? Icons.visibility_off : Icons.visibility),
              onPressed: onToggleObscure,
            ),
          ),
        ),
      ],
      actions: [
        TextButton(onPressed: onBack, child: const Text('Back')),
        TextButton(onPressed: onNext, child: const Text('Skip')),
        FilledButton(onPressed: onNext, child: const Text('Next')),
      ],
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Page 3: Spotify Setup
// ─────────────────────────────────────────────────────────────────────────────

class _SpotifySetupPage extends StatelessWidget {
  final TextEditingController idCtrl;
  final TextEditingController secretCtrl;
  final VoidCallback onNext;
  final VoidCallback onSkip;
  final VoidCallback onBack;

  const _SpotifySetupPage({
    required this.idCtrl,
    required this.secretCtrl,
    required this.onNext,
    required this.onSkip,
    required this.onBack,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return _WizardPage(
      icon: Icons.music_note,
      iconColor: const Color(0xFF1DB954),
      title: 'Spotify Setup',
      subtitle: 'Optional — needed for private playlists & liked songs',
      children: [
        Container(
          padding: const EdgeInsets.all(12),
          margin: const EdgeInsets.only(bottom: 16),
          decoration: BoxDecoration(
            color: const Color(0xFF1DB954).withOpacity(0.08),
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: const Color(0xFF1DB954).withOpacity(0.3)),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('How to create a Spotify application:',
                  style: theme.textTheme.labelLarge),
              const SizedBox(height: 8),
              const _InstructionStep(
                num: '1',
                text: 'Go to https://developer.spotify.com/dashboard and log in.',
              ),
              const _InstructionStep(
                num: '2',
                text: 'Click "Create App". Enter any name and description.',
              ),
              const _InstructionStep(
                num: '3',
                text: 'In "Redirect URIs", add exactly:\n    http://127.0.0.1:48721/callback',
              ),
              const _InstructionStep(
                num: '4',
                text: 'Click "Save". Then open your new app and click "Settings".',
              ),
              const _InstructionStep(
                num: '5',
                text: 'Copy the "Client ID" and reveal & copy the "Client Secret".',
              ),
              const _InstructionStep(
                num: '6',
                text: 'Paste them below. On first use, sldl will open a browser to authorize. '
                    'Copy the resulting token and refresh token back into Settings.',
              ),
              const SizedBox(height: 6),
              InkWell(
                onTap: () async {
                  final uri = Uri.parse(
                      'https://developer.spotify.com/dashboard');
                  if (await canLaunchUrl(uri)) launchUrl(uri);
                },
                child: Text(
                  'Open Spotify Developer Dashboard →',
                  style: TextStyle(
                    color: const Color(0xFF1DB954),
                    decoration: TextDecoration.underline,
                    decorationColor: const Color(0xFF1DB954),
                  ),
                ),
              ),
            ],
          ),
        ),
        TextField(
          controller: idCtrl,
          decoration: const InputDecoration(
            labelText: 'Client ID',
            border: OutlineInputBorder(),
          ),
        ),
        const SizedBox(height: 12),
        TextField(
          controller: secretCtrl,
          obscureText: true,
          decoration: const InputDecoration(
            labelText: 'Client Secret',
            border: OutlineInputBorder(),
          ),
        ),
      ],
      actions: [
        TextButton(onPressed: onBack, child: const Text('Back')),
        TextButton(onPressed: onSkip, child: const Text('Skip')),
        FilledButton(onPressed: onNext, child: const Text('Next')),
      ],
    );
  }
}

class _InstructionStep extends StatelessWidget {
  final String num;
  final String text;
  const _InstructionStep({required this.num, required this.text});

  @override
  Widget build(BuildContext context) {
    return Padding(
      padding: const EdgeInsets.only(bottom: 6),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Container(
            width: 20,
            height: 20,
            alignment: Alignment.center,
            margin: const EdgeInsets.only(right: 8, top: 1),
            decoration: BoxDecoration(
              color: Theme.of(context).colorScheme.primary.withOpacity(0.15),
              shape: BoxShape.circle,
            ),
            child: Text(num,
                style: TextStyle(
                    fontSize: 11,
                    fontWeight: FontWeight.bold,
                    color: Theme.of(context).colorScheme.primary)),
          ),
          Expanded(
            child: Text(text, style: const TextStyle(fontSize: 13)),
          ),
        ],
      ),
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Page 4: YouTube Setup
// ─────────────────────────────────────────────────────────────────────────────

class _YouTubeSetupPage extends StatelessWidget {
  final TextEditingController keyCtrl;
  final VoidCallback onNext;
  final VoidCallback onSkip;
  final VoidCallback onBack;

  const _YouTubeSetupPage({
    required this.keyCtrl,
    required this.onNext,
    required this.onSkip,
    required this.onBack,
  });

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return _WizardPage(
      icon: Icons.smart_display,
      iconColor: Colors.red,
      title: 'YouTube Setup',
      subtitle: 'Optional — improves playlist retrieval reliability',
      children: [
        Container(
          padding: const EdgeInsets.all(12),
          margin: const EdgeInsets.only(bottom: 16),
          decoration: BoxDecoration(
            color: Colors.red.withOpacity(0.06),
            borderRadius: BorderRadius.circular(8),
            border: Border.all(color: Colors.red.withOpacity(0.3)),
          ),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text('YouTube Data API v3 Key (optional):',
                  style: theme.textTheme.labelLarge),
              const SizedBox(height: 6),
              const Text(
                'Without a key, sldl can still download YouTube playlists but may miss some videos. '
                'A free API key enables complete and reliable playlist fetching.\n\n'
                'How to get a key:',
                style: TextStyle(fontSize: 13),
              ),
              const SizedBox(height: 6),
              const _InstructionStep(
                  num: '1',
                  text: 'Go to https://console.cloud.google.com and sign in.'),
              const _InstructionStep(
                  num: '2',
                  text: 'Create a new project (or select an existing one).'),
              const _InstructionStep(
                  num: '3',
                  text:
                      'Click "Enable APIs & Services" and search for "YouTube Data API v3". Enable it.'),
              const _InstructionStep(
                  num: '4',
                  text: 'Go to Credentials → Create Credentials → API Key.'),
              const _InstructionStep(
                  num: '5', text: 'Copy the API key and paste it below.'),
              const SizedBox(height: 6),
              InkWell(
                onTap: () async {
                  final uri =
                      Uri.parse('https://console.cloud.google.com');
                  if (await canLaunchUrl(uri)) launchUrl(uri);
                },
                child: const Text(
                  'Open Google Cloud Console →',
                  style: TextStyle(
                    color: Colors.red,
                    decoration: TextDecoration.underline,
                    decorationColor: Colors.red,
                  ),
                ),
              ),
            ],
          ),
        ),
        TextField(
          controller: keyCtrl,
          decoration: const InputDecoration(
            labelText: 'YouTube Data API Key',
            border: OutlineInputBorder(),
          ),
        ),
      ],
      actions: [
        TextButton(onPressed: onBack, child: const Text('Back')),
        TextButton(onPressed: onSkip, child: const Text('Skip')),
        FilledButton(onPressed: onNext, child: const Text('Next')),
      ],
    );
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Page 5: Done
// ─────────────────────────────────────────────────────────────────────────────

class _DonePage extends StatelessWidget {
  final VoidCallback onFinish;
  const _DonePage({required this.onFinish});

  @override
  Widget build(BuildContext context) {
    final theme = Theme.of(context);

    return _WizardPage(
      icon: Icons.check_circle_outline,
      iconColor: Colors.green,
      title: 'All Set!',
      subtitle: 'sldl UI is ready to use',
      children: [
        Text(
          'Your settings have been saved. You can update any of them later via the Settings button in the main screen.',
          style: theme.textTheme.bodyMedium,
        ),
        const SizedBox(height: 16),
        Text(
          'Quick start tips:',
          style: theme.textTheme.labelLarge,
        ),
        const SizedBox(height: 8),
        _FeatureRow(icon: Icons.link, text: 'Paste a Spotify or YouTube playlist URL to download it'),
        _FeatureRow(icon: Icons.search, text: 'Type "Artist - Album" and enable Album mode for album downloads'),
        _FeatureRow(icon: Icons.favorite_border, text: 'Use "spotify-likes" to download your Spotify liked songs'),
        _FeatureRow(icon: Icons.album, text: 'Enable Aggregate + Album for full artist discographies'),
        const SizedBox(height: 16),
        Container(
          padding: const EdgeInsets.all(10),
          decoration: BoxDecoration(
            color: theme.colorScheme.tertiaryContainer.withOpacity(0.4),
            borderRadius: BorderRadius.circular(8),
          ),
          child: Text(
            'Note: sldl does not share your music. Please also run a Soulseek client (e.g. Nicotine+) to share your collection and keep the network healthy.',
            style: theme.textTheme.bodySmall,
          ),
        ),
      ],
      actions: [
        FilledButton.icon(
          icon: const Icon(Icons.launch),
          label: const Text('Start Downloading'),
          onPressed: onFinish,
        ),
      ],
    );
  }
}
