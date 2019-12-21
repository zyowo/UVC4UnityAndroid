﻿#define ENABLE_LOG

using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

#if UNITY_ANDROID && UNITY_2018_3_OR_NEWER
using UnityEngine.Android;
#endif

using static Serenegiant.UVC.UVCEventHandler;

namespace Serenegiant.UVC {

	[RequireComponent(typeof(AndroidUtils))]
	public class UVCManager : MonoBehaviour
	{
		private const string TAG = "UVCManager#";
		private const string FQCN_PLUGIN = "com.serenegiant.uvcplugin.DeviceDetector";

		/**
		 * UVC機器が接続されたときのイベント
		 * @param UVCManager
		 * @param UVCInfo 接続されたUVC機器情報
		 * @return bool 接続されたUVC機器を使用するかどうか
		 */
		public IOnUVCAttachHandler OnAttachEventHandler;
		/**
		 * UVC機器が取り外されたときのイベント
		 * @param UVCManager
		 * @param UVCInfo 取り外されるUVC機器情報
		 */
		public IOnUVCDetachHandler OnDetachEventHandler;
		/**
		 * 解像度の選択処理
		 */
		public IOnUVCSelectSizeHandler OnUVCSelectSizeHandler;
		/**
		 * 映像取得開始時の処理
		 * @param UVCManager
		 * @param UVCInfo 取り外されるUVC機器情報
		 * @param texture 映像を受け取るTextureオブジェクト
		 */
		public IOnUVCStartHandler OnStartPreviewEventHandler;
		/**
		 * 映像取得終了時の処理
		 */
		public IOnUVCStopHandler OnStopPreviewEventHandler;


		/**
		 * IUVCSelectorがセットされていないとき
		 * またはIUVCSelectorが解像度選択時にnullを
		 * 返したときのデフォルトの解像度(幅)
		 */
		public int DefaultWidth = 1280;
		/**
		 * IUVCSelectorがセットされていないとき
		 * またはIUVCSelectorが解像度選択時にnullを
		 * 返したときのデフォルトの解像度(高さ)
		 */
		public int DefaultHeight = 720;
		/**
		 * UVC機器とのネゴシエーション時に
		 * H.264を優先してネゴシエーションするかどうか
		 * Android実機のみ有効
		 * true:	H.264 > MJPEG > YUV
		 * false:	MJPEG > H.264 > YUV
		 */
		public bool PreferH264 = true;

		/**
		 * プラグインでのレンダーイベント取得用native(c/c++)関数
		 */
		[DllImport("uvc-plugin")]
		private static extern IntPtr GetRenderEventFunc();

		private class CameraInfo
		{
			public readonly UVCInfo info;
			/**
			 * プレビュー中のUVCカメラ識別子, レンダーイベント用
			 */
			public Int32 activeCameraId;
			public Texture previewTexture;

			public CameraInfo(UVCInfo info)
			{
				this.info = info;
			}
		
			/**
			 * レンダーイベント処理用
			 * コールーチンとして実行される
			 */
			public IEnumerator OnRender()
			{
				var renderEventFunc = GetRenderEventFunc();
				for (; ; )
				{
					yield return new WaitForEndOfFrame();
					GL.IssuePluginEvent(renderEventFunc, activeCameraId);
				}
			}
		}

		/**
		 * ハンドリングしているカメラ情報を保持
		 * string(deviceName) - CameraInfo ペアを保持する
		 */
		private Dictionary<string, CameraInfo> cameraInfos = new Dictionary<string, CameraInfo>();

		//================================================================================
		// UnityEngineからの呼び出し

		IEnumerator Start()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Start:");
#endif
			yield return Initialize();
		}

		void OnApplicationFocus()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnApplicationFocus:");
#endif
		}

		void OnApplicationPause(bool pauseStatus)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnApplicationPause:{pauseStatus}");
#endif
		}

		void OnApplicationQuits()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnApplicationQuits:");
#endif
		}

		void OnDestroy()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnDestroy:");
#endif
			CloseAll();
		}

		//================================================================================
		/**
		 * カメラをopenしているか
		 * 映像取得中かどうかはIsPreviewingを使うこと
		 */
		public bool IsOpen(string deviceName)
		{
			var info = Get(deviceName);
			return (info != null) && (info.activeCameraId != 0);
		}

		/**
		 * 映像取得中かどうか
		 */
		public bool IsPreviewing(string deviceName)
		{
			var info = Get(deviceName);
			return (info != null) && (info.activeCameraId != 0) && (info.previewTexture != null);
		}

		/**
		 * 映像取得用のTextureオブジェクトを取得する
		 * @return Textureオブジェクト, プレビュー中でなければnull
		 */
		public Texture GetTexture(string deviceName)
		{
			var info = Get(deviceName);
			return info != null ? info.previewTexture : null;
		}

		//================================================================================
		// Android固有の処理
		// Java側からのイベントコールバック

		/**
		 * UVC機器が接続された
		 * @param args UVC機器識別文字列
		 */
		public void OnEventAttach(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventAttach[{Time.frameCount}]:(" + args + ")");
#endif
			if (!String.IsNullOrEmpty(args))
			{   // argsはdeviceName
				var info = CreateIfNotExist(args);
				if ((OnAttachEventHandler == null) 
					|| OnAttachEventHandler.OnUVCAttachEvent(this, info.info))
				{
					RequestUsbPermission(args);
				} else
				{
					Remove(args);
				}
			}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventAttach[{Time.frameCount}]:finished");
#endif
		}

		/**
		 * UVC機器が取り外された
		 * @param args UVC機器識別文字列
		 */
		public void OnEventDetach(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventDetach:({args})");
#endif
			var info = Get(args);
			if ((info != null) && (OnDetachEventHandler != null))
			{
				OnDetachEventHandler.OnUVCDetachEvent(this, info.info);
				Close(args);
				Remove(args);
			}
		}

		/**
		 * UVC機器へのアクセスのためのパーミッションを取得できた
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventPermission(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventPermission:({args})");
#endif
			if (!String.IsNullOrEmpty(args))
			{   // argsはdeviceName
				Open(args);
			}
		}

		/**
		 * UVC機器をオープンした
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventConnect(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventConnect:({args})");
#endif
		}

		/**
		 * UVC機器をクローズした
		 * @param args UVC機器の識別文字列
		 */
		public void OnEventDisconnect(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventDisconnect:({args})");
#endif
			// このイベントはUnity側からclose要求を送ったとき以外でも発生するので
			// 念のためにCloseを呼んでおく
			Close(args);
		}

		/**
 * 映像を受け取れるようになった
 * @param args UVC機器の識別文字列
 */
		public void OnEventReady(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnEventReady:({args})");
#endif
			StartPreview(args);
		}

		/**
		 * UVC機器からの映像取得を開始した
		 * @param args UVC機器の識別文字列
		 */
		public void OnStartPreview(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnStartPreview:({args})");
#endif
			var info = Get(args);
			if ((info != null) && (OnStartPreviewEventHandler != null))
			{
				OnStartPreviewEventHandler.OnUVCStartEvent(this, info.info, GetTexture(args));
			}
		}

		/**
		 * UVC機器からの映像取得を終了した
		 * @param args UVC機器の識別文字列
		 */
		public void OnStopPreview(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnStopPreview:({args})");
#endif
			var info = Get(args);
			if ((info != null) && (OnStopPreviewEventHandler != null))
			{
				OnStopPreviewEventHandler.OnUVCStopEvent(this, info.info);
			}
		}

		/**
		 * UVC機器からのステータスイベントを受信した
		 * @param args UVC機器識別文字列＋ステータス
		 */
		public void OnReceiveStatus(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnReceiveStatus:({args})");
#endif
		}

		/**
		 * UVC機器からのボタンイベントを受信した
		 * @param args UVC機器識別文字列＋ボタンイベント
		 */
		public void OnButtonEvent(string args)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnButtonEvent:({args})");
#endif
		}

		/**
		 * onResumeイベント
		 */
		public IEnumerator OnResumeEvent()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnResumeEvent:" +
				$"isPermissionRequesting={AndroidUtils.isPermissionRequesting}");
#endif
			if (!AndroidUtils.isPermissionRequesting
				&& AndroidUtils.CheckAndroidVersion(28)
				&& !AndroidUtils.HasPermission(AndroidUtils.PERMISSION_CAMERA))
			{
				yield return Initialize();
			}

			if (!AndroidUtils.isPermissionRequesting)
			{	// パーミッション要求中ではないとき
				foreach (var elm in cameraInfos)
				{
					if (elm.Value.activeCameraId == 0)
					{	// アタッチされた機器があるけどオープンされていないとき
						RequestUsbPermission(elm.Key);
						break;
					}
				}
			}

			yield break;
		}

		/**
		 * onPauseイベント
		 */
		public void OnPauseEvent()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}OnPauseEvent:");
#endif
			CloseAll();
		}

		//--------------------------------------------------------------------------------
		public IEnumerator Initialize()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Initialize:");
#endif
			if (AndroidUtils.CheckAndroidVersion(28))
			{
				yield return AndroidUtils.GrantCameraPermission((string permission, AndroidUtils.PermissionGrantResult result) =>
				{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
					Console.WriteLine($"{TAG}OnPermission:{permission}={result}");
#endif
					switch (result)
					{
						case AndroidUtils.PermissionGrantResult.PERMISSION_GRANT:
							InitPlugin();
							break;
						case AndroidUtils.PermissionGrantResult.PERMISSION_DENY:
							if (AndroidUtils.ShouldShowRequestPermissionRationale(AndroidUtils.PERMISSION_CAMERA))
							{
								// パーミッションを取得できなかった
								// FIXME 説明用のダイアログ等を表示しないといけない
							}
							break;
						case AndroidUtils.PermissionGrantResult.PERMISSION_DENY_AND_NEVER_ASK_AGAIN:
							break;
					}
				});
			} else
			{
				InitPlugin();
			}

			yield break;
		}

		// uvc-plugin-unityへの処理要求
		/**
		 * プラグインを初期化
		 */
		private void InitPlugin()
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}InitPlugin:");
#endif
			if (OnAttachEventHandler == null)
			{
				OnAttachEventHandler = GetComponent(typeof(IOnUVCAttachHandler)) as IOnUVCAttachHandler;
			}
			if (OnDetachEventHandler == null)
			{
				OnDetachEventHandler = GetComponent(typeof(IOnUVCDetachHandler)) as IOnUVCDetachHandler;
			}
			if (OnStartPreviewEventHandler == null)
			{
				OnStartPreviewEventHandler = GetComponent(typeof(IOnUVCStartHandler)) as IOnUVCStartHandler;
			}
			if (OnStopPreviewEventHandler == null)
			{
				OnStopPreviewEventHandler = GetComponent(typeof(IOnUVCStopHandler)) as IOnUVCStopHandler;
			}
			if (OnUVCSelectSizeHandler == null)
			{
				OnUVCSelectSizeHandler = GetComponent(typeof(IOnUVCSelectSizeHandler)) as IOnUVCSelectSizeHandler;
			}
			using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
			{
				clazz.CallStatic("initDeviceDetector",
					AndroidUtils.GetCurrentActivity(), gameObject.name);
			}
		}

		/**
		 * 指定したUSB機器をアクセスするパーミッションを持っているかどうかを取得
		 * @param deviceName UVC機器識別文字列
		 */
		private bool HasUsbPermission(string deviceName)
		{
			if (!String.IsNullOrEmpty(deviceName))
			{
				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					return clazz.CallStatic<bool>("hasUsbPermission",
						AndroidUtils.GetCurrentActivity(), deviceName);
				}
			}
			else
			{
				return false;
			}
		}

		/**
		 * USB機器アクセスのパーミッション要求
		 * @param deviceName UVC機器識別文字列
		 */
		private void RequestUsbPermission(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}RequestUsbPermission[{Time.frameCount}]:({deviceName})");
#endif
			if (!String.IsNullOrEmpty(deviceName))
			{
				AndroidUtils.isPermissionRequesting = true;

				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					clazz.CallStatic("requestPermission",
						AndroidUtils.GetCurrentActivity(), deviceName);
				}
			}
			else
			{
				throw new ArgumentException("device name is empty/null");
			}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}RequestUsbPermission[{Time.frameCount}]:finsihed");
#endif
		}

		/**
		 * 指定したUVC機器をopenする
		 * @param deviceName UVC機器識別文字列
		 */
		private void Open(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Open:{deviceName}");
#endif
			var info = Get(deviceName);
			if (info != null)
			{
				AndroidUtils.isPermissionRequesting = false;
				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					info.activeCameraId = clazz.CallStatic<Int32>("openDevice",
						AndroidUtils.GetCurrentActivity(), deviceName,
						DefaultWidth, DefaultHeight, PreferH264);
				}
			}
			else
			{
				throw new ArgumentException("device name is empty/null");
			}
		}

		/**
		 * 指定したUVC機器をcloseする
		 * @param deviceName UVC機器識別文字列
		 */
		public void Close(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Close:{deviceName}");
#endif
			var info = Get(deviceName);
			if (info != null)
			{
				info.activeCameraId = 0;
				info.previewTexture = null;
			}
			if (!String.IsNullOrEmpty(deviceName))
			{
				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					clazz.CallStatic("closeDevice",
						AndroidUtils.GetCurrentActivity(), deviceName);
				}
			}
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}Close:finished");
#endif
		}

		/**
		 * OpenしているすべてのUVC機器をCloseする
		 */
		private void CloseAll()
		{
			List<string> keys = new List<string>(cameraInfos.Keys);
			foreach (var deviceName in keys)
			{
				Close(deviceName);
			}
		}

		/**
		 * UVC機器からの映像受け取り開始要求をする
		 * @param deviceName UVC機器識別文字列
		 */
		private void StartPreview(string deviceName)
		{
			int width = DefaultWidth;
			int height = DefaultHeight;

			var supportedVideoSize = GetSupportedVideoSize(deviceName);
			if (supportedVideoSize == null)
			{
				throw new ArgumentException("fauled to get supported video size");
			}

			// 解像度の選択処理
			if (OnUVCSelectSizeHandler != null)
			{
				var size = OnUVCSelectSizeHandler.OnUVCSelectSize(this, GetInfo(deviceName), supportedVideoSize);
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}StartPreview:selected={size}");
#endif
				if (size != null)
				{
					width = size.Width;
					height = size.Height;
				}
			}

			// 対応解像度のチェック
			if (supportedVideoSize.Find(width, height/*,minFps=0.1f, maxFps=121.0f*/) == null)
			{   // 指定した解像度に対応していない
#if (!NDEBUG && DEBUG && ENABLE_LOG)
				Console.WriteLine($"{TAG}StartPreview:{width}x{height} is NOT supported.");
				Console.WriteLine($"{TAG}Info={GetInfo(deviceName)}");
				Console.WriteLine($"{TAG}supportedVideoSize={supportedVideoSize}");
#endif
				throw new ArgumentOutOfRangeException($"{width}x{height} is NOT supported.");
			}
			StartPreview(deviceName, width, height);
		}

		/**
		 * UVC機器からの映像受け取り開始要求をする
		 * この関数では指定したサイズに対応しているかどうかのチェックをしないので
		 * 呼び出し元でチェックすること
		 * 通常はStartPreview(string deviceName)経由で呼び出す
		 * @param deviceName UVC機器識別文字列
		 * @param width
		 * @param height
		 */
		private void StartPreview(string deviceName, int width, int height)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}StartPreview:{deviceName}({width}x{height})");
#endif
			if (!IsPreviewing(deviceName))
			{
				var info = Get(deviceName);
				if (info != null)
				{
					info.previewTexture = new Texture2D(
							width, height,
							TextureFormat.ARGB32,
							false, /* mipmap */
							true /* linear */);
					var nativeTexPtr = info.previewTexture.GetNativeTexturePtr();
#if (!NDEBUG && DEBUG && ENABLE_LOG)
					Console.WriteLine($"{TAG}RequestStartPreview:tex={nativeTexPtr}");
#endif
					using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
					{
						clazz.CallStatic("setPreviewTexture",
							AndroidUtils.GetCurrentActivity(), deviceName,
							nativeTexPtr.ToInt32(),
							-1,	// PreviewMode, -1:自動選択(Open時に指定したPreferH264フラグが有効になる)
							width, height);
					}

					StartCoroutine(info.OnRender());
				}
				else
				{
					throw new ArgumentException("device name is empty/null");
				}
			}
		}

		/**
 * UVC機器/カメラからの映像受けとりを終了要求をする
 * @param deviceName UVC機器識別文字列
 */
		private void StopPreview(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}StopPreview:{deviceName}");
#endif
			var info = Get(deviceName);
			if (info != null)
			{
				StopCoroutine(info.OnRender());
			}
			RequestStopPreview(deviceName);
		}

		/**
		 * UVC機器からの映像受けとりを終了要求をする
		 * @param deviceName UVC機器識別文字列
		 */
		private void RequestStopPreview(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}RequestStopPreviewUVC:{deviceName}");
#endif
			if (!String.IsNullOrEmpty(deviceName))
			{
				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					clazz.CallStatic("stopPreview",
						AndroidUtils.GetCurrentActivity(), deviceName);
				}
			}
		}

		/**
		 * 指定したUVC機器の情報(今はvidとpid)をJSON文字列として取得する
		 * @param deviceName UVC機器識別文字列
		 */
		private UVCInfo GetInfo(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}GetInfo:{deviceName}");
#endif

			if (!String.IsNullOrEmpty(deviceName))
			{
				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					return UVCInfo.Parse(deviceName,
						clazz.CallStatic<string>("getInfo",
							AndroidUtils.GetCurrentActivity(), deviceName));
				}
			}
			else
			{
				throw new ArgumentException("device name is empty/null");
			}

		}

		/**
		 * 指定したUVC機器の対応解像度を取得する
		 * @param deviceName UVC機器識別文字列
		 */
		public SupportedFormats GetSupportedVideoSize(string deviceName)
		{
#if (!NDEBUG && DEBUG && ENABLE_LOG)
			Console.WriteLine($"{TAG}GetSupportedVideoSize:{deviceName}");
#endif

			if (!String.IsNullOrEmpty(deviceName))
			{
				using (AndroidJavaClass clazz = new AndroidJavaClass(FQCN_PLUGIN))
				{
					return SupportedFormats.Parse(
						clazz.CallStatic<string>("getSupportedVideoSize",
							AndroidUtils.GetCurrentActivity(), deviceName));
				}
			}
			else
			{
				throw new ArgumentException("device name is empty/null");
			}
		}

		/*NonNull*/
		private CameraInfo CreateIfNotExist(string deviceName)
		{
			if (!cameraInfos.ContainsKey(deviceName))
			{
				cameraInfos[deviceName] = new CameraInfo(GetInfo(deviceName));
			}
			return cameraInfos[deviceName];
		}

		/*Nullable*/
		private CameraInfo Get(string deviceName)
		{
			return cameraInfos.ContainsKey(deviceName) ? cameraInfos[deviceName] : null;
		}

		/*Nullable*/
		private CameraInfo Remove(string deviceName)
		{
			CameraInfo info = null;

			if (cameraInfos.ContainsKey(deviceName))
			{
				info = cameraInfos[deviceName];
				cameraInfos.Remove(deviceName);
			}
	
			return info;
		}
	

	} // UVCManager

}   // namespace Serenegiant.UVC
