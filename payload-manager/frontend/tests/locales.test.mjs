import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile, readdir } from 'node:fs/promises';
import { createInstance } from 'i18next';
import { fallbackCardinalRule, legacyPluralSupport, LANGUAGE_NAMES } from '../src/utils/localeSupport.js';

const folder = new URL('../src/locales/', import.meta.url);
const resources = {};
for (const filename of await readdir(folder)) {
  if (filename.endsWith('.json')) resources[filename.slice(0, -5)] = { translation: JSON.parse(await readFile(new URL(filename, folder), 'utf8')) };
}
const english = resources['en-US'].translation;
const plural = /^(storage_hub\.(?:files|payload)_count)_(zero|one|two|few|many|other)$/;
const placeholders = text => [...text.matchAll(/{{\s*([^}]+?)\s*}}/g)].map(m => m[1]).sort();
const markup = text => [...text.matchAll(/<\/?(?:\d+|strong)>/g)].map(m => m[0]).sort();

for (const [locale, { translation }] of Object.entries(resources)) {
  test(`${locale}: active keys, placeholders, markup and cardinal forms`, () => {
    assert.ok(LANGUAGE_NAMES[locale]);
    for (const [key, value] of Object.entries(english)) {
      if (plural.test(key)) continue;
      assert.equal(typeof translation[key], 'string', `${locale}: ${key}`);
      assert.ok(translation[key].trim(), `${locale}: empty ${key}`);
      assert.deepEqual(placeholders(translation[key]), placeholders(value), `${locale}: ${key} interpolation`);
      assert.deepEqual(markup(translation[key]), markup(value), `${locale}: ${key} React markup`);
    }
    const categories = new Intl.PluralRules(locale).resolvedOptions().pluralCategories;
    for (const base of ['storage_hub.files_count', 'storage_hub.payload_count']) {
      for (const category of categories) {
        const key = `${base}_${category}`;
        assert.ok(translation[key], `${locale}: missing ${key}`);
        assert.deepEqual(placeholders(translation[key]), ['count'], `${locale}: ${key}`);
      }
    }
    for (const key of Object.keys(translation)) {
      assert.ok(key in english || plural.test(key), `${locale}: obsolete ${key}`);
    }
  });
}

test('source consumers have English entries, including dynamically selected categories/counts', async () => {
  async function walk(dir) {
    const result = [];
    for (const entry of await readdir(dir, { withFileTypes: true })) {
      const path = new URL(entry.name + (entry.isDirectory() ? '/' : ''), dir);
      if (entry.isDirectory()) result.push(...await walk(path));
      else if (/\.(jsx|js)$/.test(entry.name)) result.push(await readFile(path, 'utf8'));
    }
    return result;
  }
  const source = (await walk(new URL('../src/', import.meta.url))).join('\n');
  const keys = [...source.matchAll(/\bt\(\s*["']([^"']+)["']|i18nKey=["']([^"']+)["']/g)].map(m => m[1] || m[2]);
  for (const key of keys) assert.ok(key in english || `${key}_other` in english, `missing active English key: ${key}`);
  for (const category of ['Utilities & Tools', 'Loaders', 'System & Jailbreak', 'Networking & Servers']) assert.ok(english[`category.${category}`]);
});

test('Dutch has reviewed translations with explicit legitimate English-identical terms', () => {
  const legitimate = new Set(['app.nav.dashboard', 'app.nav.auto_mobile', 'app.dashboard.title_2',
    'storage_hub.update_badge', 'payload_detail.readme_title', 'category.Loaders',
    'storage_hub.payload_count_one', 'storage_hub.payload_count_other']);
  for (const [key, value] of Object.entries(resources['nl-NL'].translation)) {
    if (value === english[key]) assert.ok(legitimate.has(key), `Dutch untranslated: ${key}`);
  }
});

test('legacy cardinal rules agree with native Intl for supported UI counts', () => {
  const counts = [...Array.from({ length: 201 }, (_, i) => i), 1000, 1000000, 2000000, 0.5, 1.5, 2.5, 3.5, 11.5];
  for (const locale of Object.keys(resources)) {
    const native = new Intl.PluralRules(locale), fallback = fallbackCardinalRule(locale);
    assert.deepEqual(fallback.resolvedOptions().pluralCategories.sort(), native.resolvedOptions().pluralCategories.sort(), locale);
    for (const count of counts) assert.equal(fallback.select(count), native.select(count), `${locale}: ${count}`);
  }
});

test('i18next chooses correct count forms with and without Intl.PluralRules', async () => {
  const native = Intl.PluralRules;
  try {
    for (const legacy of [false, true]) {
      if (legacy) Intl.PluralRules = undefined;
      const instance = createInstance().use(legacyPluralSupport);
      await instance.init({ resources, fallbackLng: 'en-US', keySeparator: false });
      for (const locale of Object.keys(resources)) {
        for (const count of [0, 1, 2, 3, 5, 11, 21, 22, 101, 1000000]) {
          const category = new native(locale).select(count);
          for (const base of ['storage_hub.files_count', 'storage_hub.payload_count']) {
            assert.equal(instance.t(base, { lng: locale, count }), resources[locale].translation[`${base}_${category}`].replace('{{count}}', count), `${locale}: ${count}, legacy=${legacy}`);
          }
        }
      }
    }
  } finally { Intl.PluralRules = native; }
});
