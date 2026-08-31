<script lang="ts">
  import { getThemePreference, setThemePreference, type ThemePreference } from '../lib/theme';

  const themeOptions: { value: ThemePreference; label: string }[] = [
    { value: 'auto', label: 'Auto' },
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
  ];

  let theme = $state<ThemePreference>(getThemePreference());

  function chooseTheme(next: ThemePreference): void {
    theme = next;
    setThemePreference(next);
  }
</script>

<section class="page settings-page">
  <header class="page-heading">
    <p class="eyebrow">Configuration</p>
    <h1>Settings</h1>
  </header>

  <section class="settings-section" aria-labelledby="appearance-heading">
    <header class="settings-section-heading">
      <h2 id="appearance-heading">Appearance</h2>
    </header>

    <div class="settings-row">
      <div class="settings-row-copy">
        <strong>Theme</strong>
        <span>Auto follows your system appearance.</span>
      </div>

      <div class="settings-theme-options" role="radiogroup" aria-label="Theme">
        {#each themeOptions as option}
          <button
            type="button"
            role="radio"
            aria-checked={theme === option.value}
            class:active={theme === option.value}
            onclick={() => chooseTheme(option.value)}
          >
            {option.label}
          </button>
        {/each}
      </div>
    </div>
  </section>
</section>
