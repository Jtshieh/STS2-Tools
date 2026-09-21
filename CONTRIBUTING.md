# Contributing

English | [简体中文](CONTRIBUTING.zh-CN.md)

Contributions that improve policy integration, recording, replay, installation, and documentation are welcome.

## Report a problem

Open an [issue](https://github.com/Jtshieh/STS2-Tools/issues/new/choose) with:

- Repository commit or release, game version, OS, and CPU architecture.
- The entry point and command, with personal paths replaced by placeholders.
- Steps to reproduce, expected behavior, and the actual error.
- For a replay mismatch, the event/action sequence and differing field names.

Share a minimal synthetic example or a redacted error excerpt. Keep game binaries, assets, saves, full gameplay logs, account identifiers, and credentials out of issues and pull requests.

## Make a change

Use a branch and keep each pull request focused on one behavior. Explain the trigger, the resulting behavior, and the validation you performed. Reuse existing runtime evidence when a change does not affect the relevant execution path.

Keep English and Chinese usage pages consistent. Add source files to `RELEASE_FILES.txt`; files added under `linux/` also belong in its release list and `SOURCE_ORIGINS.json`. Preserve upstream attribution.

Choose the checks relevant to your change. From the repository root:

```bash
python3 -B scripts/audit_source.py
python3 -B -m unittest discover -s mac/tests -v
```

On Linux, from `linux/`:

```bash
python3 -B -m unittest discover -s tests -v
python3 -B scripts/package_source.py
```

Protocol fixtures should be synthetic. A runtime or replay change may also need a bounded check using your own compatible game and profile. State what was exercised and retain failed or interrupted outcomes when reporting the result.

Contributions use the project's [MIT license](LICENSE), with existing [third-party attribution](NOTICE.md) retained.
