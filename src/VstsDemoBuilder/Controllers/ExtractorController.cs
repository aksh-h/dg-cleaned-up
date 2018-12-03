﻿using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Web.Mvc;
using VstsDemoBuilder.Extensions;
using VstsDemoBuilder.ExtractorModels;
using VstsDemoBuilder.Models;
using VstsRestAPI;
using VstsRestAPI.Extractor;
using VstsRestAPI.ProjectsAndTeams;
using VstsRestAPI.Viewmodel.Extractor;
using VstsRestAPI.Viewmodel.ProjectAndTeams;
using VstsRestAPI.WorkItemAndTracking;

namespace VstsDemoBuilder.Controllers
{

    public class ExtractorController : Controller
    {
        private AccessDetails accessDetails = new AccessDetails();
        private EnvironmentController con = new EnvironmentController();
        private static object objLock = new object();
        private static Dictionary<string, string> statusMessages;

        private delegate string[] ProcessEnvironment(Project model);

        private ExtractorAnalysis analysis = new ExtractorAnalysis();
        private string projectSelectedToExtract = string.Empty;
        public void AddMessage(string id, string message)
        {
            lock (objLock)
            {
                if (id.EndsWith("_Errors"))
                {
                    StatusMessages[id] = (StatusMessages.ContainsKey(id) ? StatusMessages[id] : string.Empty) + message;
                }
                else
                {
                    StatusMessages[id] = message;
                }
            }
        }
        private static Dictionary<string, string> StatusMessages
        {
            get
            {
                if (statusMessages == null)
                {
                    statusMessages = new Dictionary<string, string>();
                }

                return statusMessages;
            }
            set
            {
                statusMessages = value;
            }
        }
        [AllowAnonymous]
        public ActionResult NotFound()
        {
            return View();
        }

        // Extractor index page
        [AllowAnonymous]
        public ActionResult Index(ProjectList.ProjectDetails model)
        {
            string email = Session["Email"].ToString();
            string pat = "";
            if (Session["PAT"] != null)
            {
                pat = Session["PAT"].ToString();
            }
            if (string.IsNullOrEmpty(pat))
            {
                return Redirect("../Account/Verify");
            }
            else
            {
                accessDetails.access_token = pat;
                ProfileDetails profile = con.GetProfile(accessDetails);
                Session["User"] = profile.displayName;
                Session["PAT"] = pat;
                AccountsResponse.AccountList accountList = con.GetAccounts(profile.id, accessDetails);
                model.accessToken = accessDetails.access_token;
                model.accountsForDropdown = new List<string>();
                if (accountList.count > 0)
                {
                    foreach (var account in accountList.value)
                    {
                        model.accountsForDropdown.Add(account.accountName);
                    }
                    model.accountsForDropdown.Sort();
                }
                else
                {
                    model.accountsForDropdown.Add("Select Organization");
                    ViewBag.AccDDError = "Could not load your organizations. Please change the directory in profile page of Azure DevOps Organization and try again.";
                }
                return View(model);
            }
        }

        // Get the current progress of work done
        [HttpGet]
        [AllowAnonymous]
        public ContentResult GetCurrentProgress(string id)
        {
            this.ControllerContext.HttpContext.Response.AddHeader("cache-control", "no-cache");
            var currentProgress = GetStatusMessage(id).ToString();
            return Content(currentProgress);
        }

        // Get status message to display
        [HttpGet]
        [AllowAnonymous]
        public string GetStatusMessage(string id)
        {
            lock (objLock)
            {
                string message = string.Empty;
                if (StatusMessages.Keys.Count(x => x == id) == 1)
                {
                    message = StatusMessages[id];
                }
                else
                {
                    return "100";
                }

                if (id.EndsWith("_Errors"))
                {
                    RemoveKey(id);
                }

                return message;
            }
        }

        public void RemoveKey(string id)
        {
            lock (objLock)
            {
                StatusMessages.Remove(id);
            }
        }

        // Get Project List from the selected Organization
        [AllowAnonymous]
        public JsonResult GetprojectList(string accname, string pat)
        {
            string defaultHost = System.Configuration.ConfigurationManager.AppSettings["DefaultHost"];
            string ProjectCreationVersion = System.Configuration.ConfigurationManager.AppSettings["ProjectCreationVersion"];


            Configuration config = new Configuration() { AccountName = accname, PersonalAccessToken = pat, UriString = defaultHost + accname, VersionNumber = ProjectCreationVersion };
            Projects projects = new Projects(config);
            ProjectsResponse.ProjectResult projectResult = projects.GetListOfProjects();
            try
            {
                if (string.IsNullOrEmpty(projectResult.errmsg))
                {
                    if (projectResult.count == 0)
                    {
                        projectResult.errmsg = "No projects found!";
                    }
                    return Json(projectResult, JsonRequestBehavior.AllowGet);
                }
            }
            catch (Exception ex)
            {
                projectResult.errmsg = ex.Message.ToString();
                string message = ex.Message.ToString();
            }
            return Json(projectResult, JsonRequestBehavior.AllowGet);
        }

        //Get Project Properties to knwo which process template it is following
        [AllowAnonymous]
        public JsonResult GetProjectProperties(string accname, string project, string _credentials)
        {
            string defaultHost = System.Configuration.ConfigurationManager.AppSettings["DefaultHost"];
            string url = defaultHost + accname;
            ProjectProperties.Properties load = new ProjectProperties.Properties();
            try
            {
                using (var client = new HttpClient())
                {
                    string version = System.Configuration.ConfigurationManager.AppSettings["ProjectProperties"];
                    client.BaseAddress = new Uri(url);
                    client.DefaultRequestHeaders.Accept.Clear();
                    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("appication/json"));
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _credentials);
                    HttpResponseMessage response = client.GetAsync(url + "/_apis/projects/" + project + "/properties?api-version=" + version).Result;
                    if (response.IsSuccessStatusCode)
                    {
                        string res = response.Content.ReadAsStringAsync().Result;
                        load = JsonConvert.DeserializeObject<ProjectProperties.Properties>(res);
                        GetProcessTemplate.PTemplate template = new GetProcessTemplate.PTemplate();

                        string processTypeId = string.Empty;
                        var processTypeID = load.value.Where(x => x.name == "System.ProcessTemplateType").FirstOrDefault();
                        if (processTypeID != null)
                        {
                            processTypeId = processTypeID.value;
                        }

                        using (var client1 = new HttpClient())
                        {
                            client1.BaseAddress = new Uri(url);
                            client1.DefaultRequestHeaders.Accept.Clear();
                            client1.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("appication/json"));
                            client1.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _credentials);
                            HttpResponseMessage response1 = client1.GetAsync(url + "/_apis/work/processes/" + processTypeId + "?api-version=" + version).Result;
                            if (response1.IsSuccessStatusCode && response.StatusCode == HttpStatusCode.OK)
                            {
                                string templateData = response1.Content.ReadAsStringAsync().Result;
                                template = JsonConvert.DeserializeObject<GetProcessTemplate.PTemplate>(templateData);
                                load.TypeClass = template.properties.Class;
                            }
                        }
                        return Json(load, JsonRequestBehavior.AllowGet);

                    }
                    else
                    {
                        var result = response.Content.ReadAsStringAsync().Result;
                        return Json(result, JsonRequestBehavior.AllowGet);
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(ex.Message, JsonRequestBehavior.AllowGet);
            }
        }

        //Inintiate the extraction process
        [AllowAnonymous]
        public bool StartExtractionProcess(Project model)
        {
            return true;
        }

        // End the extraction process
        public void EndEnvironmentSetupProcess(IAsyncResult result)
        {
            ProcessEnvironment processTask = (ProcessEnvironment)result.AsyncState;
            string[] strResult = processTask.EndInvoke(result);
            RemoveKey(strResult[0]);
            if (StatusMessages.Keys.Count(x => x == strResult[0] + "_Errors") == 1)
            {
                string errorMessages = statusMessages[strResult[0] + "_Errors"];
                if (errorMessages != "")
                {
                    //also, log message to file system
                    string logPath = Server.MapPath("~") + @"\Log";
                    string accountName = strResult[1];
                    string fileName = string.Format("{0}_{1}.txt", projectSelectedToExtract, DateTime.Now.ToString("ddMMMyyyy_HHmmss"));

                    if (!Directory.Exists(logPath))
                    {
                        Directory.CreateDirectory(logPath);
                    }

                    System.IO.File.AppendAllText(Path.Combine(logPath, fileName), errorMessages);

                    //Create ISSUE work item with error details in VSTSProjectgenarator account
                    string patBase64 = System.Configuration.ConfigurationManager.AppSettings["PATBase64"];
                    string url = System.Configuration.ConfigurationManager.AppSettings["URL"];
                    string projectId = System.Configuration.ConfigurationManager.AppSettings["PROJECTID"];
                    string issueName = string.Format("{0}_{1}", projectSelectedToExtract, DateTime.Now.ToString("ddMMMyyyy_HHmmss"));
                    IssueWI objIssue = new IssueWI();

                    errorMessages = errorMessages + Environment.NewLine + "TemplateUsed: " + projectSelectedToExtract;

                    string LogWIT = "true";//System.Configuration.ConfigurationManager.AppSettings["LogWIT"];
                    if (LogWIT == "true")
                    {
                        objIssue.CreateIssueWI(patBase64, "1.0", url, issueName, errorMessages, projectId);
                    }
                }
            }
        }

        //Analyze the selected project to know what all the artifacts it has
        public static ProjectConfigurations ProjectConfiguration(Project model)
        {
            string repoVersion = System.Configuration.ConfigurationManager.AppSettings["RepoVersion"];
            string buildVersion = System.Configuration.ConfigurationManager.AppSettings["BuildVersion"];
            string releaseVersion = System.Configuration.ConfigurationManager.AppSettings["ReleaseVersion"];
            string wikiVersion = System.Configuration.ConfigurationManager.AppSettings["WikiVersion"];
            string boardVersion = System.Configuration.ConfigurationManager.AppSettings["BoardVersion"];
            string workItemsVersion = System.Configuration.ConfigurationManager.AppSettings["WorkItemsVersion"];
            string releaseHost = System.Configuration.ConfigurationManager.AppSettings["ReleaseHost"];
            string defaultHost = System.Configuration.ConfigurationManager.AppSettings["DefaultHost"];
            string getReleaseVersion = System.Configuration.ConfigurationManager.AppSettings["GetRelease"];
            string agentQueueVersion = System.Configuration.ConfigurationManager.AppSettings["AgentQueueVersion"];
            ProjectConfigurations projectConfig = new ProjectConfigurations();

            projectConfig.AgentQueueConfig = new Configuration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = wikiVersion };
            projectConfig.WorkItemConfig = new Configuration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = wikiVersion };
            projectConfig.BuildDefinitionConfig = new Configuration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = buildVersion };
            projectConfig.ReleaseDefinitionConfig = new Configuration() { UriString = releaseHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = releaseVersion };
            projectConfig.RepoConfig = new Configuration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = repoVersion };
            projectConfig.BoardConfig = new Configuration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = boardVersion };
            projectConfig.Config = new Configuration() { UriString = defaultHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id };
            projectConfig.GetReleaseConfig = new Configuration() { UriString = releaseHost + model.accountName + "/", PersonalAccessToken = model.accessToken, Project = model.ProjectName, AccountName = model.accountName, Id = model.id, VersionNumber = getReleaseVersion };

            return projectConfig;
        }

        [AllowAnonymous]
        public JsonResult AnalyzeProject(Project model)
        {
            ProjectConfigurationDetails.AppConfig = ProjectConfiguration(model);
            projectSelectedToExtract = model.ProjectName;
            analysis.teamCount = GetTeamsCount(ProjectConfigurationDetails.AppConfig.BoardConfig);
            analysis.IterationCount = GetIterationsCount(ProjectConfigurationDetails.AppConfig.BoardConfig);
            analysis.WorkItemCounts = GetWorkItemsCount(ProjectConfigurationDetails.AppConfig.WorkItemConfig);
            GetBuildDefinitionCount(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            GetReleaseDefinitionCount(ProjectConfigurationDetails.AppConfig.ReleaseDefinitionConfig);
            return Json(analysis, JsonRequestBehavior.AllowGet);
        }

        //Initiate the extraction process
        [HttpPost]
        [AllowAnonymous]
        public bool StartEnvironmentSetupProcess(Project model)
        {
            System.Web.HttpContext.Current.Session["Project"] = model.ProjectName;

            ProcessEnvironment processTask = new ProcessEnvironment(GenerateTemplateArifacts);
            processTask.BeginInvoke(model, new AsyncCallback(EndEnvironmentSetupProcess), processTask);
            return true;
        }

        //Extract the project artifacts
        [AllowAnonymous]
        public string[] GenerateTemplateArifacts(Project model)
        {
            ProjectConfigurationDetails.AppConfig = ProjectConfiguration(model);
            AddMessage(model.id, "Teams Definition");

            bool isTeam = GetTeamList(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);

            bool isIteration = GetIterations(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            if (isIteration)
            {
                AddMessage(model.id, "Iterations Definition");
            }
            string projectSetting = "";
            projectSetting = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\ProjectSettings.json");
            projectSetting = projectSetting.Replace("$type$", model.ProcessTemplate);
            System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + model.ProjectName + "\\ProjectSettings.json", projectSetting);

            string Extensions = "";
            Extensions = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\DemoExtensions.json");
            Extensions = projectSetting.Replace("$type$", model.ProcessTemplate);
            System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + model.ProjectName + "\\DemoExtensions.json", Extensions);


            string projectTemplate = "";
            projectTemplate = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\ProjectTemplate.json");
            System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + model.ProjectName + "\\ProjectTemplate.json", projectTemplate);

            string teamArea = "";
            teamArea = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\TeamArea.json");
            System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + model.ProjectName + "\\TeamArea.json", teamArea);
            AddMessage(model.id, "Team Areas Definition");

            GetWorkItems(ProjectConfigurationDetails.AppConfig.WorkItemConfig);
            AddMessage(model.id, "Work Items Definition");

            GetRepositoryList(ProjectConfigurationDetails.AppConfig.RepoConfig);
            AddMessage(model.id, "Repository and Service Endpoint Definition");

            //int count = GetBuildDefinitions(_buildDefinitionConfig, _repoConfig);
            //if (count >= 1)
            //{
            //    AddMessage(model.id, "Build Definition");
            //}

            //System.Threading.Thread.Sleep(2000);

            ////int relCount = GetReleaseDefinitions(_releaseDefinitionConfig);
            //int relCount = GeneralizingGetReleaseDefinitions(_getReleaseConfig, _agentQueueConfig);
            //if (relCount >= 1)
            //{
            //    AddMessage(model.id, "Release Definition");
            //    System.Threading.Thread.Sleep(2000);
            //}

            ////Export Board Rows
            ExportboardRows(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);

            //Export Card style
            ExportCardStyle(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig, model.ProcessTemplate);

            //Export Board column json for Scrum and Agile            
            System.Threading.Thread.Sleep(2000);
            if (model.ProcessTemplate == "Scrum")
            {
                GetBoardColumnsScrum(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            }
            else if (model.ProcessTemplate == "Agile")
            {
                GetBoardColumnsAgile(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            }

            //Export Card style json            
            if (model.ProcessTemplate == "Scrum")
            {
                ExportCardFieldsScrum(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            }
            else if (model.ProcessTemplate == "Agile")
            {
                ExportCardFieldsAgile(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            }

            GetTeamSetting(ProjectConfigurationDetails.AppConfig.BuildDefinitionConfig);
            string startPath = Path.Combine(Server.MapPath("~") + @"ExtractedTemplate\", model.ProjectName);

            string zipPath = Path.Combine(Server.MapPath("~") + @"ExtractedTemplate\", model.ProjectName + ".zip");
            if (System.IO.File.Exists(zipPath))
            {
                System.IO.File.Delete(zipPath);
            }
            zipPath = Path.Combine(Server.MapPath("~") + @"ExtractedTemplate\", model.ProjectName + ".zip");
            ZipFile.CreateFromDirectory(startPath, zipPath);
            Directory.Delete(Path.Combine(Server.MapPath("~") + @"ExtractedTemplate\", model.ProjectName), true);
            StatusMessages[model.id] = "100";
            return new string[] { model.id, "" };
        }

        // Get Teams Count
        [AllowAnonymous]
        public int GetTeamsCount(Configuration con)
        {
            ListTeams.TeamList teamObj = new ListTeams.TeamList();
            SrcTeamsList _team = new SrcTeamsList();
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            int count = nodes.GetTeamsCount();
            return count;
        }
        // Get Iteration Count
        public int GetIterationsCount(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            GetINumIteration.Iterations iterations = new GetINumIteration.Iterations();
            iterations = nodes.GetiterationCount();
            if (iterations.count > 0)
            {
                return iterations.count;
            }
            else
            {
                if (!(string.IsNullOrEmpty(nodes.LastFailureMessage)))
                {
                    return 0;
                }
                return 0;
            }

        }

        // Get Work Items Details
        public Dictionary<string, int> GetWorkItemsCount(Configuration con)
        {
            string[] workItemtypes = { "Epic", "Feature", "Product Backlog Item", "Task", "Test Case", "Bug", "User Story", "Test Suite", "Test Plan" };
            GetWorkItemsCount itemsCount = new GetWorkItemsCount(con);
            Dictionary<string, int> fetchedWorkItemsCount = new Dictionary<string, int>();
            if (workItemtypes.Length > 0)
            {
                foreach (var workItem in workItemtypes)
                {
                    WorkItemFetchResponse.WorkItems WITCount = itemsCount.GetWorkItemsfromSource(workItem);
                    if (WITCount.count > 0)
                    {
                        fetchedWorkItemsCount.Add(workItem, WITCount.count);
                    }
                }
            }

            return fetchedWorkItemsCount;
        }

        //Get Build Definitions count
        public void GetBuildDefinitionCount(Configuration con)
        {
            GetBuildandReleaseDefs buildandReleaseDefs = new GetBuildandReleaseDefs(con);
            GetBuildDefResponse.BuildDef buildDef = new GetBuildDefResponse.BuildDef();
            buildDef = buildandReleaseDefs.GetBuildDefCount();
            if (buildDef.count > 0)
            {
                analysis.BuildDefCount = buildDef.count;
            }
            else
            {
                analysis.BuildDefCount = 0;
            }
        }

        // Get Release Definitions count
        public void GetReleaseDefinitionCount(Configuration con)
        {
            GetBuildandReleaseDefs buildandReleaseDefs = new GetBuildandReleaseDefs(con);
            GetReleaseDefResponse.ReleaseDef releaseDef = new GetReleaseDefResponse.ReleaseDef();
            releaseDef = buildandReleaseDefs.GetReleaseDefCount();
            if (releaseDef.count > 0)
            {
                analysis.ReleaseDefCount = releaseDef.count;
            }
            else
            {
                analysis.ReleaseDefCount = 0;
            }
        }

        // Get Team List to write into file
        public bool GetTeamList(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            SrcTeamsList _team = new SrcTeamsList();

            _team = nodes.GetTeamList();
            if (_team.value != null)
            {
                string fetchedJson = JsonConvert.SerializeObject(_team, Formatting.Indented);
                if (fetchedJson != "")
                {
                    fetchedJson = fetchedJson.Remove(0, 14);
                    fetchedJson = fetchedJson.Remove(fetchedJson.Length - 1);
                    if (!Directory.Exists(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project))
                    {
                        Directory.CreateDirectory(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project);
                    }

                    System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\Teams.json", fetchedJson);
                    return true;
                }
                else if (!string.IsNullOrEmpty(nodes.LastFailureMessage))
                {
                    AddMessage(con.Id.ErrorId(), nodes.LastFailureMessage);
                    string error = nodes.LastFailureMessage;
                    return false;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                AddMessage(con.Id.ErrorId(), nodes.LastFailureMessage);
                return false;
            }
        }

        // Get Iteration List to write into file
        public bool GetIterations(VstsRestAPI.Configuration con)
        {
            try
            {
                GetClassificationNodes nodes = new GetClassificationNodes(con);
                ItearationList.Iterations viewModel = new ItearationList.Iterations();
                viewModel = nodes.GetIterations();
                string fetchedJson = JsonConvert.SerializeObject(viewModel, Formatting.Indented);
                if (fetchedJson != "")
                {
                    if (!Directory.Exists(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project))
                    {
                        Directory.CreateDirectory(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project);
                    }

                    System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\Iterations.json", fetchedJson);
                    return true;
                }
                else
                {
                    string error = nodes.LastFailureMessage;
                    AddMessage(con.Id.ErrorId(), error);

                    return false;
                }
            }
            catch (Exception)
            {
            }
            return false;
        }

        // Get Work items to write into file
        public void GetWorkItems(Configuration con)
        {
            if (!Directory.Exists(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project))
            {
                Directory.CreateDirectory(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project);
            }

            GetWorkItemsCount itemsCount = new GetWorkItemsCount(con);
            WorkItemFetchResponse.WorkItems fetchedEpics = itemsCount.GetWorkItemsfromSource("Epic");
            string epicJson = JsonConvert.SerializeObject(fetchedEpics, Formatting.Indented);
            if (epicJson != null || epicJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\EpicfromTemplate.json", epicJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }

            WorkItemFetchResponse.WorkItems fetchedFeatures = itemsCount.GetWorkItemsfromSource("Feature");
            string featureJson = JsonConvert.SerializeObject(fetchedFeatures, Formatting.Indented);
            if (featureJson != null || featureJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\FeaturefromTemplate.json", featureJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }

            WorkItemFetchResponse.WorkItems fetchedPBIs = itemsCount.GetWorkItemsfromSource("Product Backlog Item");
            string pbiJson = JsonConvert.SerializeObject(fetchedPBIs, Formatting.Indented);
            if (pbiJson != null || pbiJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\PBIfromTemplate.json", pbiJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }

            WorkItemFetchResponse.WorkItems fetchedTasks = itemsCount.GetWorkItemsfromSource("Task");
            string taskJson = JsonConvert.SerializeObject(fetchedTasks, Formatting.Indented);
            if (taskJson != null || taskJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\TaskfromTemplate.json", taskJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }

            WorkItemFetchResponse.WorkItems fetchedTestCase = itemsCount.GetWorkItemsfromSource("Test Case");
            string testCasesJson = JsonConvert.SerializeObject(fetchedTestCase, Formatting.Indented);
            if (testCasesJson != null || testCasesJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\TestCasefromTemplate.json", testCasesJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }

            WorkItemFetchResponse.WorkItems fetchedBugs = itemsCount.GetWorkItemsfromSource("Bug");
            string bugJson = JsonConvert.SerializeObject(fetchedBugs, Formatting.Indented);
            if (bugJson != null || bugJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\BugfromTemplate.json", bugJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }

            WorkItemFetchResponse.WorkItems fetchedUserStories = itemsCount.GetWorkItemsfromSource("User Story");
            string userStoryJson = JsonConvert.SerializeObject(fetchedUserStories, Formatting.Indented);
            if (userStoryJson != null || userStoryJson != "")
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\UserStoriesfromTemplate.json", userStoryJson);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), itemsCount.LastFailureMessage);
            }
        }

        // Get Repository list to create Service end point json with respect to the repositiory
        // and also create the import source code json
        // It works only for the user who is having access to both Source and Target repositories in the organization with the same UserID
        public void GetRepositoryList(Configuration con)
        {
            GetBuildandReleaseDefs buildandReleaseDefs = new GetBuildandReleaseDefs(con);
            RepositoryList.Repository repos = buildandReleaseDefs.GetRepoList();
            if (repos.count > 0)
            {
                foreach (var repo in repos.value)
                {
                    string defaultHost = System.Configuration.ConfigurationManager.AppSettings["DefaultHost"];
                    string host = defaultHost + con.AccountName + "/" + con.Project;
                    string sourceCodeJson = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\ImportSourceCode.json");
                    sourceCodeJson = sourceCodeJson.Replace("$Host$", host).Replace("$Repo$", repo.name);
                    string endPointJson = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\ServiceEndPoint.json");
                    endPointJson = endPointJson.Replace("$Host$", host).Replace("$Repo$", repo.name);
                    if (!Directory.Exists(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ImportSourceCode"))
                    {
                        Directory.CreateDirectory(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ImportSourceCode");
                        System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ImportSourceCode\\" + repo.name + ".json", sourceCodeJson);
                    }
                    else
                    {
                        System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ImportSourceCode\\" + repo.name + ".json", sourceCodeJson);
                    }
                    if (!Directory.Exists(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints"))
                    {
                        Directory.CreateDirectory(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints");
                        System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints\\" + repo.name + "-code.json", endPointJson);
                    }
                    else
                    {
                        System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints\\" + repo.name + "-code.json", endPointJson);
                    }

                }
            }
        }

        // Get the Build definitions to write into file
        public int GetBuildDefinitions(Configuration con, Configuration repoCon)
        {
            GetBuildandReleaseDefs buildandReleaseDefs = new GetBuildandReleaseDefs(con);
            List<JObject> builds = buildandReleaseDefs.ExportBuildDefinitions();
            GetBuildandReleaseDefs repoDefs = new GetBuildandReleaseDefs(repoCon);
            RepositoryList.Repository repo = repoDefs.GetRepoList();
            if (builds.Count > 0)
            {
                int count = 1;
                //creating ImportCode Json file
                string templatePath = Server.MapPath("~") + @"ExtractedTemplate\" + con.Project;
                foreach (JObject def in builds)
                {
                    string repoID = "";
                    var repoName = def["repository"]["name"];
                    foreach (var re in repo.value)
                    {
                        if (re.name == repoName.ToString())
                        {
                            repoID = re.id;
                        }
                    }
                    def["authoredBy"] = "{}";
                    def["authoredBy"] = "{}";
                    def["project"] = "{}";
                    def["url"] = "";
                    def["uri"] = "";
                    def["id"] = "";
                    def["queue"]["id"] = "";
                    def["queue"]["pool"]["id"] = "";
                    def["_links"] = "{}";
                    def["createdDate"] = "";
                    var type = def["repository"]["type"];
                    if (type.ToString().ToLower() == "github")
                    {
                        def["repository"]["type"] = "Git";
                        def["repository"]["properties"]["fullName"] = "repository";
                        def["repository"]["properties"]["connectedServiceId"] = "$GitHub$";
                        def["repository"]["name"] = "repository";
                        string url = def["repository"]["url"].ToString();
                        if (url != "")
                        {
                            string endPointString = System.IO.File.ReadAllText(Server.MapPath("~") + @"PreSetting\\GitHubEndPoint.json");
                            endPointString = endPointString.Replace("$GitHubURL$", url);
                            Guid g = Guid.NewGuid();
                            string randStr = g.ToString().Substring(0, 8); if (!Directory.Exists(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints"))
                            {
                                Directory.CreateDirectory(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints");
                                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\ServiceEndpoints\\GitHub-" + randStr + "-EndPoint.json", endPointString);
                            }
                        }
                    }
                    else
                    {
                        def["repository"]["id"] = "$" + repoName + "$";
                        def["repository"]["url"] = "";
                    }
                    var input = def["processParameters"]["inputs"];
                    if (input.HasValues)
                    {
                        foreach (var i in input)
                        {
                            i["defaultValue"] = "";

                        }
                    }

                    var build = def["build"];
                    if (build.HasValues)
                    {
                        foreach (var b in build)
                        {
                            b["inputs"]["serverEndpoint"] = "";
                        }
                    }

                    if (!(Directory.Exists(templatePath + "\\BuildDefinitions")))
                    {
                        Directory.CreateDirectory(templatePath + "\\BuildDefinitions");
                        System.IO.File.WriteAllText(templatePath + "\\BuildDefinitions\\BuildDef" + count + ".json", JsonConvert.SerializeObject(def, Formatting.Indented));
                    }
                    else
                    {
                        System.IO.File.WriteAllText(templatePath + "\\BuildDefinitions\\BuildDef" + count + ".json", JsonConvert.SerializeObject(def, Formatting.Indented));
                    }
                    count = count + 1;
                }
                return count;
            }
            return 0;
        }

        // Get the release definition to write into file
        public int GetReleaseDefinitions(Configuration con)
        {
            GetBuildandReleaseDefs releaseDefs = new GetBuildandReleaseDefs(con);
            List<JObject> releases = releaseDefs.GetReleaseDefs();
            Dictionary<string, int> queue = releaseDefs.GetQueues();
            string templatePath = Server.MapPath("~") + @"ExtractedTemplate\" + con.Project;
            if (releases.Count > 0)
            {
                int count = 1;
                foreach (JObject rel in releases)
                {
                    ReleaseDefResponse.Response responseObj = new ReleaseDefResponse.Response();
                    responseObj = JsonConvert.DeserializeObject<ReleaseDefResponse.Response>(rel.ToString());
                    foreach (var ownerObj in responseObj.environments)
                    {
                        ownerObj.owner.id = "$OwnerId$";
                        ownerObj.owner.displayName = "$OwnerDisplayName$";
                        ownerObj.owner.uniqueName = "$OwnerUniqueName$";
                        if (ownerObj.deployPhases.Count > 0)
                        {
                            foreach (var deployPhase in ownerObj.deployPhases)
                            {
                                string queueName = "";
                                if (queue != null)
                                {
                                    if (queue.Count > 0)
                                    {
                                        var agenetName = queue.Where(x => x.Value.ToString() == deployPhase.deploymentInput.queueId).FirstOrDefault();
                                        if (agenetName.Key != null)
                                        {
                                            queueName = agenetName.Key.ToString();
                                        }
                                        else
                                        {
                                            queueName = "Hosted VS2017";
                                        }
                                    }
                                }
                                if (queueName != "")
                                {
                                    deployPhase.deploymentInput.queueId = "$" + queueName + "$";
                                }
                                else
                                {
                                    deployPhase.deploymentInput.queueId = "";
                                }
                                if (deployPhase.workflowTasks.Count > 0)
                                {
                                    foreach (var workFlow in deployPhase.workflowTasks)
                                    {
                                        workFlow.inputs.ConnectedServiceName = "";
                                        workFlow.inputs.ConnectedServiceNameARM = "";
                                        workFlow.inputs.deploymentGroupEndpoint = "";
                                    }
                                }
                            }
                        }
                    }
                    if (responseObj.artifacts.Count > 0)
                    {
                        foreach (var artifact in responseObj.artifacts)
                        {
                            string buildName = artifact.definitionReference.definition.name;

                            artifact.sourceId = "$ProjectId$:" + "$" + buildName + "-id$";
                            artifact.definitionReference.definition.id = "$" + buildName + "-id$";

                            artifact.definitionReference.project.id = "$ProjectId$";
                            artifact.definitionReference.project.name = "$ProjectName$";
                        }
                        if (!(Directory.Exists(templatePath + "\\ReleaseDefinitions")))
                        {
                            Directory.CreateDirectory(templatePath + "\\ReleaseDefinitions");
                            System.IO.File.WriteAllText(templatePath + "\\ReleaseDefinitions\\ReleaseDef" + count + ".json", JsonConvert.SerializeObject(responseObj, Formatting.Indented));
                        }
                        else
                        {
                            System.IO.File.WriteAllText(templatePath + "\\ReleaseDefinitions\\ReleaseDef" + count + ".json", JsonConvert.SerializeObject(responseObj, Formatting.Indented));
                        }
                    }
                    count++;
                }
                return count;
            }
            return 1;
        }

        // Generalizing the release definition method to make it work for All kind of Release definition
        public int GeneralizingGetReleaseDefinitions(Configuration con, Configuration _agentQueue)
        {
            try
            {
                GetBuildandReleaseDefs releaseDefs = new GetBuildandReleaseDefs(con);
                List<JObject> releases = releaseDefs.GetReleaseDefs();
                GetBuildandReleaseDefs agent = new GetBuildandReleaseDefs(_agentQueue);

                Dictionary<string, int> queue = agent.GetQueues();
                string templatePath = Server.MapPath("~") + @"ExtractedTemplate\" + con.Project;
                int releasecount = 1;
                if (releases.Count > 0)
                {
                    foreach (JObject rel in releases)
                    {
                        rel["id"] = "";
                        rel["url"] = "";
                        rel["_links"] = "{}";
                        rel["createdBy"] = "{}";
                        rel["createdOn"] = "";
                        rel["modifiedBy"] = "{}";
                        rel["modifiedOn"] = "";
                        var env = rel["environments"];
                        foreach (var e in env)
                        {
                            e["badgeUrl"] = "";
                            var owner = e["owner"];
                            owner["id"] = "$OwnerId$";
                            owner["displayName"] = "$OwnerDisplayName$";
                            owner["uniqueName"] = "$OwnerUniqueName$";
                            owner["url"] = "";
                            owner["_links"] = "{}";
                            owner["imageUrl"] = "";
                            owner["descriptor"] = "";

                            var deployPhases = e["deployPhases"];
                            if (deployPhases.HasValues)
                            {
                                foreach (var dep in deployPhases)
                                {

                                    var deploymentInput = dep["deploymentInput"];
                                    var queueID = deploymentInput["queueId"];
                                    string queueName = "";
                                    if (queue != null)
                                    {
                                        if (queue.Count > 0)
                                        {
                                            var agenetName = queue.Where(x => x.Value.ToString() == queueID.ToString()).FirstOrDefault();
                                            if (agenetName.Key != null)
                                            {
                                                queueName = agenetName.Key.ToString();
                                            }
                                            else
                                            {
                                                queueName = "";
                                            }
                                        }
                                    }
                                    if (queueName != "")
                                    {
                                        deploymentInput["queueId"] = "$" + queueName + "$";
                                    }
                                    else
                                    {
                                        deploymentInput["queueId"] = "";
                                    }

                                    var workflow = dep["workflowTasks"];
                                    if (workflow.HasValues)
                                    {
                                        foreach (var flow in workflow)
                                        {
                                            var input = flow["inputs"];
                                            string keyConfig = System.IO.File.ReadAllText(Server.MapPath("~") + @"\\Templates\EndpointKeyConfig.json");
                                            KeyConfig.Keys keyC = new KeyConfig.Keys();
                                            keyC = JsonConvert.DeserializeObject<KeyConfig.Keys>(keyConfig);
                                            foreach (var key in keyC.keys)
                                            {
                                                input[key] = "";
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        var artifact = rel["artifacts"];
                        if (artifact.HasValues)
                        {
                            foreach (var art in artifact)
                            {
                                string buildName = art["definitionReference"]["definition"]["name"].ToString();

                                art["sourceId"] = "$ProjectId$:" + "$" + buildName + "-id$";
                                art["definitionReference"]["definition"]["id"] = "$" + buildName + "-id$";
                                art["definitionReference"]["project"]["id"] = "$ProjectId$";
                                art["definitionReference"]["project"]["name"] = "$ProjectName$";
                                art["definitionReference"]["artifactSourceDefinitionUrl"] = "{}";
                            }
                        }
                        if (!(Directory.Exists(templatePath + "\\ReleaseDefinitions")))
                        {
                            Directory.CreateDirectory(templatePath + "\\ReleaseDefinitions");
                            System.IO.File.WriteAllText(templatePath + "\\ReleaseDefinitions\\ReleaseDef" + releasecount + ".json", JsonConvert.SerializeObject(rel, Formatting.Indented));
                        }
                        else
                        {
                            System.IO.File.WriteAllText(templatePath + "\\ReleaseDefinitions\\ReleaseDef" + releasecount + ".json", JsonConvert.SerializeObject(rel, Formatting.Indented));
                        }
                        releasecount++;
                    }
                }
                return releasecount;
            }
            catch (Exception ex)
            {
                AddMessage(con.Id.ErrorId(), ex.Message + Environment.NewLine + ex.StackTrace);
            }
            return 0;
        }

        // Get Agile project Board column details
        public void GetBoardColumnsAgile(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            BoardColumnResponseAgile.ColumnResponse responseAgile = new BoardColumnResponseAgile.ColumnResponse();
            responseAgile = nodes.ExportBoardColumnsAgile();
            if (responseAgile.count > 0)
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\BoardColumns.json", JsonConvert.SerializeObject(responseAgile.value, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                AddMessage(con.Id, "Board Columns Definition");
                Thread.Sleep(2000);
            }
            else
            {
                if (!string.IsNullOrEmpty(nodes.LastFailureMessage))
                {
                    AddMessage(con.Id.ErrorId(), "Error While Exporting board column : " + nodes.LastFailureMessage);
                }
            }
        }

        // Get Scrum project board column details
        public void GetBoardColumnsScrum(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            BoardColumnResponseScrum.ColumnResponse responseScrum = new BoardColumnResponseScrum.ColumnResponse();
            responseScrum = nodes.ExportBoardColumnsScrum();
            if (responseScrum != null)
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\BoardColumns.json", JsonConvert.SerializeObject(responseScrum.value, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                AddMessage(con.Id, "Board Columns Definition");
                Thread.Sleep(2000);
            }
            if (!string.IsNullOrEmpty(nodes.LastFailureMessage))
            {
                AddMessage(con.Id.ErrorId(), "Error While Exporting board column : " + nodes.LastFailureMessage);
            }
        }

        // Get Board Row details to write into file
        public void ExportboardRows(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            ExportBoardRows.Rows rows = nodes.ExportboardRows();
            if (rows.count > 0)
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\BoardRowsFromTemplate.json", JsonConvert.SerializeObject(rows.value, Formatting.Indented));
                AddMessage(con.Id, "Board Rows Definition");
                Thread.Sleep(2000);
            }
            else if (!string.IsNullOrEmpty(nodes.LastFailureMessage))
            {
                AddMessage(con.Id.ErrorId(), "Error While Exporting board rows : " + nodes.LastFailureMessage);
            }
        }

        // Get Card style detials to write into file
        public void ExportCardStyle(Configuration con, string processType)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            CardStyle.Style style = new CardStyle.Style();
            string boardType = string.Empty;
            if (processType == "Scrum")
            {
                boardType = "Backlog%20Items";
            }
            else if (processType == "Agile")
            {
                boardType = "Stories";
            }
            style = nodes.GetCardStyle(boardType);
            if (style.rules != null)
            {
                if (style.rules.fill != null)
                {
                    System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\UpdateCardStyles.json", JsonConvert.SerializeObject(style, Formatting.Indented));
                    AddMessage(con.Id, "Card Style Rules Definition");
                }

                Thread.Sleep(2000);
            }
            else if (!string.IsNullOrEmpty(nodes.LastFailureMessage))
            {
                AddMessage(con.Id.ErrorId(), "Error While Exporting Card Styles : " + nodes.LastFailureMessage);
            }
        }

        // Get Card fields details to Scrum project
        public void ExportCardFieldsScrum(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            CardFiledsScrum.CardField fields = nodes.GetCardFieldsScrum();
            if (fields.cards != null)
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\UpdateCardFields.json", JsonConvert.SerializeObject(fields, Formatting.Indented));
                AddMessage(con.Id, "Card Style Rules Definition");
                Thread.Sleep(2000);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), "Error While Exporting Card fields : " + nodes.LastFailureMessage);
            }
        }

        // Get Card field details to Agile project
        public void ExportCardFieldsAgile(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            CardFiledsAgile.CardField fields = nodes.GetCardFieldsAgile();
            if (fields.cards != null)
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\UpdateCardFields.json", JsonConvert.SerializeObject(fields, Formatting.Indented, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore }));
                AddMessage(con.Id, "Card Style Rules Definition");
                Thread.Sleep(2000);
            }
            else
            {
                AddMessage(con.Id.ErrorId(), "Error While Exporting Card fields : " + nodes.LastFailureMessage);
            }
        }

        // Get the Team setting to check the Backlog board setting and Enable Epic feature
        public void GetTeamSetting(Configuration con)
        {
            GetClassificationNodes nodes = new GetClassificationNodes(con);
            GetTeamSetting.Setting setting = nodes.GetTeamSetting();
            if (setting.backlogVisibilities != null)
            {
                System.IO.File.WriteAllText(Server.MapPath("~") + @"ExtractedTemplate\" + con.Project + "\\EnableEpic.json", JsonConvert.SerializeObject(setting, Formatting.Indented));
            }
            else if (!string.IsNullOrEmpty(nodes.LastFailureMessage))
            {
                AddMessage(con.Id.ErrorId(), "Error while fetching Team Setting " + nodes.LastFailureMessage);
            }
        }

        // Remove the template folder after zipping it
        [AllowAnonymous]
        private void RemoveFolder()
        {
            string projectName = Session["Project"].ToString();
            if (projectName != "")
            {
                System.IO.File.Delete(Server.MapPath("~") + @"ExtractedTemplate\" + projectName);
                System.IO.File.Delete(Server.MapPath("~") + @"ExtractedTemplate\" + projectName + ".zip");
            }

        }

    }
}