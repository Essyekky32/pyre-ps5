<p align="center">
 <img src="./assets/icon0.png" width="128" />
</p>
<h1 align="center">PS5 Payload Manager</h1>

<p align="center">A modern, web-based dashboard to easily manage, import, and automatically load payloads on your PS5.</p>


<p align="center">
 <img src="./pldmgr_screenshot.png" width="600" />
</p>

## Features
- **Web-Based Interface**: A modern dashboard to manage payloads from your PC, phone, or the PS5 itself.
- **Import Payloads**: Easily add new payloads from a USB drive or download them from the cloud repository.
- **Automated Startup**: Set up a list of payloads to load automatically whenever you start the manager.
- **Home Screen Shortcut**: Installs a dedicated "Payload Manager" app icon to your PS5 home screen for quick access.
- **Favorites and Process Management**: Keep favorite payloads at hand, inspect active processes, and terminate eligible processes.


## Installation

### Using an Autoloader (Recommended)
It is highly recommended to use **Payload Manager** together with an **Autoloader**:

[Y2JB](https://github.com/itsPLK/ps5-y2jb-autoloader) | [BD-JB](https://github.com/itsPLK/ps5-bdjb-autoloader) | [Lua](https://github.com/itsPLK/ps5-lua-autoloader)

`pldmgr.elf` is already included as the default payload in the latest versions of the Autoloaders linked above.

If you are using an older version and don't want to update the entire autoloader, you can simply:
1. Place `pldmgr.elf` into your `autoload` directory.
2. Add `pldmgr.elf` as the only entry in your `autoload.txt` config file.

### Standalone / Manual Loading
You can also manually load the manager like any other `.elf` file. Grab the latest version from the [Releases](https://github.com/itsPLK/ps5-payload-manager/releases) page.

## Custom Repositories
You can add third-party payload repositories to the manager. To learn how to create your own repository JSON and host it, see the [Custom Repositories Guide](CUSTOM_REPOSITORIES.md).

## Translations
PS5 Payload Manager supports multiple languages. For the list of supported languages, how to report translation issues or suggest improvements, and past community translators, see [TRANSLATIONS.md](TRANSLATIONS.md).

## Credits
- [John Törnblom](https://github.com/john-tornblom) - for the [PS5 Payload SDK](https://github.com/ps5-payload-dev/sdk) and homebrew samples used as reference.
- [All contributors](https://github.com/itsPLK/ps5-payload-manager/graphs/contributors) - for their contributions to the project.
- Everyone else contributing to the PS5 homebrew scene.

## Donations
If you'd like to support my work, please check out the [DONATE.md](DONATE.md) file.

## Development
For build instructions and deployment details, see [DEVELOPMENT.md](DEVELOPMENT.md).
