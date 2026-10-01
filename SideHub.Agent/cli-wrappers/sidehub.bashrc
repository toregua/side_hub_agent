# SideHub bash init for interactive PTY sessions.
#
# Why this file exists: the agent prepends its cli-wrappers directory to PATH
# before spawning bash, but a user's ~/.bashrc commonly prepends ~/.local/bin
# (or similar) to PATH after that, shadowing the wrapper. We re-prepend
# cli-wrappers after the user's init has run so the wrapper always wins.

# Load once per shell. Keyed on the shell's pid rather than a plain flag: an
# exported flag would be inherited by every shell started from this one (an
# agent restarted from a SideHub terminal included) and make them skip init.
[ "${_SIDEHUB_BASHRC_PID:-}" = "$$" ] && return 0
_SIDEHUB_BASHRC_PID=$$

# Mirror the user's normal interactive bash init. We're invoked via
# `bash --rcfile <this> -i` (no -l), so we re-source the standard files to
# preserve user PATH, aliases, prompt, etc.
[ -f /etc/profile ] && . /etc/profile
_sidehub_profile=""
if [ -f ~/.bash_profile ]; then
  _sidehub_profile=~/.bash_profile
elif [ -f ~/.profile ]; then
  _sidehub_profile=~/.profile
fi
[ -n "$_sidehub_profile" ] && . "$_sidehub_profile"
# Most distros' .profile sources .bashrc, but not all: source it here unless
# the profile we just ran does, so it isn't run twice.
if [ -f ~/.bashrc ] && ! { [ -n "$_sidehub_profile" ] && grep -qs '\.bashrc' "$_sidehub_profile"; }; then
  . ~/.bashrc
fi
unset _sidehub_profile

# Re-prepend SideHub cli-wrappers so user PATH manipulations can't shadow it.
if [ -n "$SIDEHUB_CLI_WRAPPERS" ] && [ -d "$SIDEHUB_CLI_WRAPPERS" ]; then
  case ":$PATH:" in
    :"$SIDEHUB_CLI_WRAPPERS":*) ;;
    *) export PATH="$SIDEHUB_CLI_WRAPPERS:$PATH" ;;
  esac
fi
