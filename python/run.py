"""Convenience launcher: `python run.py` starts the app on port 5128."""
import uvicorn

if __name__ == "__main__":
    uvicorn.run("app.main:app", host="0.0.0.0", port=5128, reload=True)
