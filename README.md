# DeepSharp
![DeepSeek](https://img.shields.io/badge/DeepSeek-%235786FE.svg?style=round&logo=deepseek&logoColor=white) ![.NET](https://img.shields.io/badge/.NET-%235C2D91.svg?style=round&logo=.net&logoColor=white) ![GitHub](https://img.shields.io/badge/GitHub-%23121011.svg?style=round&logo=github&logoColor=white) ![GitHub Actions](https://img.shields.io/badge/GitHub%20Actions-%232671E5.svg?style=round&logo=githubactions&logoColor=white)

DeepSharp is a free, open source CLI wrapper for DeepSeek Chat made in C#.
Features:
| Feature | Description | State |
| ----- | ----- | ----- |
| Agent Mode | DeepSeek can edit/read files and run commands on your computer. | Partially stable |
| API | An API for other services to use. | In development |
| UI Mode | An Avalonia UI for DeepSharp. It also supports Agent Mode. | Experimental |
| Sessions | Resume previous sessions to continue working on a project after restarting. | Stable |

Commands:
| Command | Description |
| ----- | ----- |
| /resume <6char-id> | This command resumes a previous conversation created by DeepSharp. |
| /list | Lists all sessions/conversations with their ID and URL. |
| /thinking [on/off] | Disables/enables the thinking (DeepThink) |
| /help | Shows all available commands. |
| /rm <6char-id> | Deletes the conversation from DeepSeek's servers and DeepSharp's sessions. This cannot be undone. |
| /new | Creates a new session/conversation. |
| /model [expert/vision/default] | Switches the used model. Requires starting a new session. |
| /exit | Exits DeepSharp. |

## Contributing
You're more than welcome to contribute to DeepSharp by making a fork and opening a pull request! You can also open issues to report bugs or suggest new features.

> ⚠️ WARNING:
>
> DeepSharp is not affiliated or endorsed by DeepSeek. If DeepSeek's ToS changes and bans external wrappers, it is your responsibility. DeepSharp is not responsible for account bans. 