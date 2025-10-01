# Vosk Unity (Mobile Adaptation)

Big thanks to the contributors of [alphacep/vosk-unity-asr](https://github.com/alphacep/vosk-unity-asr)!  
This repository is a variant of that implementation, adapted specifically for **Mobile platforms**.

---

## Key Differences from the Source Repository
- **Remote model loading**: voice models are downloaded via Remote Addressables (selected based on the system language).  
- **UniTask integration**: replaced Coroutines with UniTasks for async operations.  
- **Mobile-ready**: several implementation details have been adjusted for mobile platforms.  
- **Custom recording controller**: improve usability.  

---

## Requirements
- Unity **2021.3 LTS** or newer  
- [UniTask](https://github.com/Cysharp/UniTask)  
- Addressables package enabled  

---

## Demo Scene
The demo scene includes a basic setup and **two sample voice models**.  
You can freely add or remove models as needed.  

> [!WARNING]  
> This feature is not tested on iOS.

---

## Adding New Models

1. **Download models** from the official Vosk repository: [https://alphacephei.com/vosk/models](https://alphacephei.com/vosk/models)  
2. **Change the file extension** of the model to `.bytes`
3. **Import models** into `Assets/RemoteModels` and mark them as **Addressables** (under the *Remote Addressables Group*).
4. **Configure paths**: set `Remote.BuildPath` and `Remote.LoadPath`
   <img width="628" height="218" alt="image" src="https://github.com/user-attachments/assets/3c734840-1b80-45a9-993c-1e6836db8232" />
5. **Add models** through the Addressables system
   
   <img width="500" height="284" alt="image" src="https://github.com/user-attachments/assets/954051c5-5e88-44a5-bcb9-8dd6d0adaae5" />
7. Build addressables and upload to your remote path
8. Your project is ready to use the new models!  

---

## License
This project follows the same license as the original [vosk-unity-asr](https://github.com/alphacep/vosk-unity-asr).  
