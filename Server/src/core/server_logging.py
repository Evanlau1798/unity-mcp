"""Process-wide server logging setup."""
import logging
import os
from logging.handlers import RotatingFileHandler
from core.config import config

class WindowsSafeRotatingFileHandler(RotatingFileHandler):
    """RotatingFileHandler that gracefully handles Windows file locking during rotation."""

    def doRollover(self):
        """Override to catch PermissionError on Windows when log file is locked."""
        try:
            super().doRollover()
        except PermissionError:
            # On Windows, another process may have the log file open.
            # Skip rotation this time - we'll try again on the next rollover.
            pass


def configure_logging():
    # Configure logging using settings from config
    logging.basicConfig(
        level=getattr(logging, config.log_level),
        format=config.log_format,
        stream=None,  # None -> defaults to sys.stderr; avoid stdout used by MCP stdio
        force=True    # Ensure our handler replaces any prior stdout handlers
    )
    logger = logging.getLogger("mcp-for-unity-server")

    # Also write logs to a rotating file so logs are available when launched via stdio.
    # Location follows OS conventions; override with UNITY_MCP_LOG_DIR.
    try:
        from utils.log_paths import resolve_log_dir
        _log_dir = resolve_log_dir()
        os.makedirs(_log_dir, exist_ok=True)
        _file_path = os.path.join(_log_dir, "unity_mcp_server.log")
        _fh = WindowsSafeRotatingFileHandler(
            _file_path, maxBytes=512*1024, backupCount=2, encoding="utf-8")
        _fh.setFormatter(logging.Formatter(config.log_format))
        _fh.setLevel(getattr(logging, config.log_level))
        logger.addHandler(_fh)
        logger.propagate = False  # Prevent double logging to root logger
        # Add file handler to root logger so __name__-based loggers (e.g. utils.focus_nudge,
        # services.tools.run_tests) also write to the log file. Named loggers with
        # propagate=False won't double-log.
        logging.getLogger().addHandler(_fh)
        # Also route telemetry logger to the same rotating file and normal level
        try:
            tlog = logging.getLogger("unity-mcp-telemetry")
            tlog.setLevel(getattr(logging, config.log_level))
            tlog.addHandler(_fh)
            tlog.propagate = False  # Prevent double logging for telemetry too
        except Exception as exc:
            # Never let logging setup break startup
            logger.debug("Failed to configure telemetry logger", exc_info=exc)
    except Exception as exc:
        # Never let logging setup break startup
        logger.debug("Failed to configure main logger file handler", exc_info=exc)
    # Quieten noisy third-party loggers to avoid clutter during stdio handshake
    for noisy in ("httpx", "urllib3", "mcp.server.lowlevel.server"):
        try:
            logging.getLogger(noisy).setLevel(
                max(logging.WARNING, getattr(logging, config.log_level)))
            logging.getLogger(noisy).propagate = False
        except Exception:
            pass

    # Import telemetry only after logging is configured to ensure its logs use stderr and proper levels
    # Ensure a slightly higher telemetry timeout unless explicitly overridden by env
    try:

        # Ensure generous timeout unless explicitly overridden by env
        if not os.environ.get("UNITY_MCP_TELEMETRY_TIMEOUT"):
            os.environ["UNITY_MCP_TELEMETRY_TIMEOUT"] = "5.0"
    except Exception:
        pass

    return logger
