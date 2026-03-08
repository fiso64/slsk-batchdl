import 'package:flutter/material.dart';
import 'package:provider/provider.dart';
import 'app.dart';
import 'providers/app_provider.dart';
import 'services/app_config_service.dart';
import 'services/config_service.dart';

void main() async {
  WidgetsFlutterBinding.ensureInitialized();

  final appConfigService = AppConfigService();
  await appConfigService.init();

  final configService = ConfigService();
  final sldlConfig = await configService.loadConfig();

  runApp(
    MultiProvider(
      providers: [
        ChangeNotifierProvider(
          create: (_) => AppProvider(
            appConfigService: appConfigService,
            configService: configService,
            initialConfig: sldlConfig,
          ),
        ),
      ],
      child: const SldlApp(),
    ),
  );
}
